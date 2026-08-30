import { createHash } from 'node:crypto'
import { readFileSync, readdirSync } from 'node:fs'
import { dirname, isAbsolute, relative, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const repositoryRoot = resolve(dirname(fileURLToPath(import.meta.url)), '../..')
const jsonOutput = process.argv.includes('--json')
const errors = []

const readJson = (relativePath) =>
  JSON.parse(readFileSync(resolve(repositoryRoot, relativePath), 'utf8'))
const normalizedEntries = (value) => Object.entries(value).sort(([left], [right]) => left.localeCompare(right))

const packageManifest = readJson('frontend/package.json')
const packageLock = readJson('frontend/package-lock.json')
const releaseLock = readJson('profiles/lock.json')
const catalog = readJson('profiles/catalog/catalog.json')
const catalogToolchains = new Map(catalog.toolchains.map((toolchain) => [toolchain.id, toolchain]))
const catalogReferenceSets = new Map(catalog.referenceSets.map((referenceSet) => [referenceSet.id, referenceSet]))
const catalogRuntimes = new Map(catalog.runtimes.map((runtime) => [runtime.id, runtime]))
const baseImages = new Map(readJson('profiles/base-images.json').images.map((image) => [image.id, image.reference]))
const packageLockRoot = packageLock.packages?.['']

if (!packageLockRoot) {
  errors.push('frontend/package-lock.json has no root package entry.')
} else {
  for (const section of ['dependencies', 'devDependencies']) {
    const declared = packageManifest[section] ?? {}
    const locked = packageLockRoot[section] ?? {}
    if (JSON.stringify(normalizedEntries(declared)) !== JSON.stringify(normalizedEntries(locked))) {
      errors.push(`frontend/package.json ${section} do not match the package-lock root entry.`)
    }
  }
}

if ('packageManager' in packageManifest) {
  errors.push('frontend/package.json must accept the supported system npm range instead of pinning a private npm executable.')
}

for (const [componentId, component] of Object.entries(releaseLock.components ?? {})) {
  if (component.kind === 'frontend' || componentId.startsWith('frontend-')) {
    errors.push(`${componentId} duplicates a direct version owned by frontend/package-lock.json.`)
  }
  if ('patchDigest' in component) {
    errors.push(`${componentId}.patchDigest is derived from patch files and must not be maintained in profiles/lock.json.`)
  }
}

const versionTools = releaseLock.components?.['const-generics-versiontools']
if (
  !versionTools ||
  versionTools.kind !== 'build-dependency' ||
  versionTools.package !== 'Microsoft.DotNet.VersionTools.Tasks' ||
  !/^sha256:[0-9a-f]{64}$/.test(versionTools.digest ?? '')
) {
  errors.push('const-generics-versiontools must be a digest-pinned top-level build dependency.')
}

const forbiddenProvenanceFields = new Set([
  'archiveSha256',
  'archiveUrl',
  'assemblySha256',
  'commit',
  'compilerVersion',
  'compilerAssetSha256',
  'legacyMetadataSha256',
  'metadataRuntimeCommit',
  'observedBuildOutputs',
  'observedReferenceContentDigests',
  'patchSeriesSha256',
  'referenceSetAttestation',
  'repository',
  'runtimeVersion',
  'source',
  'tree',
  'validatedImage',
  'validatedImageId',
  'verifiedAt',
  'verificationTimestamp'
])

const findForbiddenFields = (value, path, provenancePath) => {
  if (Array.isArray(value)) {
    value.forEach((item, index) => findForbiddenFields(item, `${path}[${index}]`, provenancePath))
    return
  }
  if (!value || typeof value !== 'object') return

  for (const [key, child] of Object.entries(value)) {
    const childPath = path ? `${path}.${key}` : key
    if (forbiddenProvenanceFields.has(key)) {
      errors.push(`${provenancePath}:${childPath} is generated output, not a maintained source input.`)
    }
    findForbiddenFields(child, childPath, provenancePath)
  }
}

const provenancePaths = readdirSync(resolve(repositoryRoot, 'profiles/provenance'), { withFileTypes: true })
  .filter((entry) => entry.isFile() && entry.name.endsWith('.json'))
  .map((entry) => `profiles/provenance/${entry.name}`)
  .sort()
const patchSeries = {}

for (const provenancePath of provenancePaths) {
  const provenance = readJson(provenancePath)
  findForbiddenFields(provenance, '', provenancePath)

  const component = releaseLock.components?.[provenance.componentId]
  const sourceComponent = releaseLock.components?.[provenance.sourceComponentId]
  if (!component) {
    errors.push(`${provenancePath}:componentId must reference profiles/lock.json.`)
  }
  if (
    !sourceComponent ||
    sourceComponent.kind !== 'source' ||
    typeof sourceComponent.resolvedVersion !== 'string' ||
    typeof sourceComponent.commit !== 'string' ||
    !/^sha256:[0-9a-f]{64}$/.test(sourceComponent.digest ?? '') ||
    typeof sourceComponent.sourceUri !== 'string'
  ) {
    errors.push(`${provenancePath}:sourceComponentId must reference a complete source identity in profiles/lock.json.`)
  }

  if (typeof provenance.builder?.imageId !== 'string' || !baseImages.has(provenance.builder.imageId)) {
    errors.push(`${provenancePath}:builder.imageId must reference profiles/base-images.json.`)
  }
  if ('image' in (provenance.builder ?? {})) {
    errors.push(`${provenancePath}:builder.image duplicates the central base-image reference.`)
  }

  const referenceSetComponentId = provenance.build?.referenceSet?.componentId ?? provenance.build?.referenceSetId
  if (
    referenceSetComponentId !== undefined &&
    (!releaseLock.components?.[referenceSetComponentId] ||
      releaseLock.components[referenceSetComponentId].kind !== 'reference-set')
  ) {
    errors.push(`${provenancePath}:build reference set must reference a release-lock reference-set component.`)
  }

  const metadataRuntimeSourceComponentId = provenance.build?.metadataRuntimeSourceComponentId
  if (
    metadataRuntimeSourceComponentId !== undefined &&
    releaseLock.components?.[metadataRuntimeSourceComponentId]?.kind !== 'source'
  ) {
    errors.push(`${provenancePath}:metadata runtime must reference a release-lock source component.`)
  }

  const operatorImageComponentId = provenance.build?.operatorImageComponentId
  if (
    operatorImageComponentId !== undefined &&
    releaseLock.components?.[operatorImageComponentId]?.kind !== 'operator-image'
  ) {
    errors.push(`${provenancePath}:operator image must reference a release-lock operator-image component.`)
  }

  const runtimeComponentId = provenance.build?.runtimeComponentId
  if (
    runtimeComponentId !== undefined &&
    releaseLock.components?.[runtimeComponentId]?.kind !== 'runtime'
  ) {
    errors.push(`${provenancePath}:build runtime must reference a release-lock runtime component.`)
  }

  if (provenance.runtimeDependency !== undefined) {
    if (releaseLock.components?.[provenance.runtimeDependency.sourceComponentId]?.kind !== 'source') {
      errors.push(`${provenancePath}:runtimeDependency.sourceComponentId must reference a source component.`)
    }
    if (releaseLock.components?.[provenance.runtimeDependency.runtimeComponentId]?.kind !== 'runtime') {
      errors.push(`${provenancePath}:runtimeDependency.runtimeComponentId must reference a runtime component.`)
    }
  }

  const artifactContract = provenance.artifactContract
  if (artifactContract !== undefined) {
    if (artifactContract.compatibilityGroup !== provenance.compatibilityGroup) {
      errors.push(`${provenancePath}:artifactContract.compatibilityGroup must match the provenance compatibilityGroup.`)
    }

    const toolchain = artifactContract.toolchainId === undefined
      ? undefined
      : catalogToolchains.get(artifactContract.toolchainId)
    if (artifactContract.toolchainId !== undefined && toolchain === undefined) {
      errors.push(`${provenancePath}:artifactContract.toolchainId must reference the Catalog.`)
    }
    if (
      toolchain !== undefined &&
      typeof provenance.build?.artifactFormat === 'string' &&
      !toolchain.producesArtifactFormats.includes(provenance.build.artifactFormat)
    ) {
      errors.push(`${provenancePath}:build.artifactFormat is not produced by artifactContract.toolchainId.`)
    }

    const referenceSet = artifactContract.referenceSetId === undefined
      ? undefined
      : catalogReferenceSets.get(artifactContract.referenceSetId)
    if (artifactContract.referenceSetId !== undefined && referenceSet === undefined) {
      errors.push(`${provenancePath}:artifactContract.referenceSetId must reference the Catalog.`)
    }
    if (referenceSet !== undefined) {
      if (
        provenance.build?.referenceSetId !== undefined &&
        provenance.build.referenceSetId !== referenceSet.id
      ) {
        errors.push(`${provenancePath}:build.referenceSetId must match artifactContract.referenceSetId.`)
      }
      if (artifactContract.targetFramework !== referenceSet.targetFramework) {
        errors.push(`${provenancePath}:artifactContract.targetFramework must match the Catalog reference set.`)
      }
      if (artifactContract.runtimeFamily !== referenceSet.runtimeFamily) {
        errors.push(`${provenancePath}:artifactContract.runtimeFamily must match the Catalog reference set.`)
      }
      const requiredTags = artifactContract.requiredRuntimeFeatureTags
      const catalogRequiredTags = referenceSet.requiredRuntimeFeatureTags ?? []
      if (
        requiredTags !== undefined &&
        JSON.stringify([...requiredTags].sort()) !== JSON.stringify([...catalogRequiredTags].sort())
      ) {
        errors.push(`${provenancePath}:artifactContract.requiredRuntimeFeatureTags must match the Catalog reference set.`)
      }
    }

    const runtime = provenance.build?.runtimeComponentId === undefined
      ? undefined
      : catalogRuntimes.get(provenance.build.runtimeComponentId)
    if (provenance.build?.runtimeComponentId !== undefined && runtime === undefined) {
      errors.push(`${provenancePath}:build.runtimeComponentId must reference the Catalog runtime.`)
    }
    if (runtime !== undefined) {
      if (artifactContract.runtimeFamily !== runtime.family) {
        errors.push(`${provenancePath}:artifactContract.runtimeFamily must match the Catalog runtime.`)
      }
      if (artifactContract.architecture !== runtime.architecture) {
        errors.push(`${provenancePath}:artifactContract.architecture must match the Catalog runtime.`)
      }
      if (
        typeof provenance.build?.artifactFormat === 'string' &&
        !runtime.acceptedArtifactFormats.includes(provenance.build.artifactFormat)
      ) {
        errors.push(`${provenancePath}:build.artifactFormat is not accepted by build.runtimeComponentId.`)
      }
    }
  }

  for (const [index, dependencyOverride] of (provenance.build?.bootstrapDependencyOverrides ?? []).entries()) {
    const componentId = dependencyOverride.componentId
    if (typeof componentId !== 'string' || !releaseLock.components?.[componentId]) {
      errors.push(`${provenancePath}:bootstrapDependencyOverrides[${index}] must reference a release-lock component.`)
    }
    for (const duplicateField of ['package', 'resolvedVersion', 'sourceUri', 'sha256']) {
      if (duplicateField in dependencyOverride) {
        errors.push(
          `${provenancePath}:bootstrapDependencyOverrides[${index}].${duplicateField} duplicates the referenced release-lock component.`
        )
      }
    }
  }

  if (!Array.isArray(provenance.patchSeries)) continue

  const seriesHash = createHash('sha256')
  const patches = []
  const declaredPatchPaths = []
  for (const [index, patch] of provenance.patchSeries.entries()) {
    if ('sha256' in patch) {
      errors.push(`${provenancePath}:patchSeries[${index}].sha256 must be computed from the patch file.`)
    }
    if (typeof patch.path !== 'string' || patch.path.length === 0) {
      errors.push(`${provenancePath}:patchSeries[${index}] has no patch path.`)
      continue
    }

    const patchPath = resolve(repositoryRoot, patch.path)
    const pathFromRoot = relative(repositoryRoot, patchPath)
    if (isAbsolute(pathFromRoot) || pathFromRoot.startsWith('..')) {
      errors.push(`${provenancePath}:patchSeries[${index}] escapes the repository root.`)
      continue
    }

    let bytes
    try {
      bytes = readFileSync(patchPath)
    } catch (error) {
      errors.push(`${provenancePath}:patchSeries[${index}] cannot be read: ${error.message}`)
      continue
    }
    seriesHash.update(bytes)
    declaredPatchPaths.push(patch.path)
    patches.push({
      path: patch.path,
      digest: `sha256:${createHash('sha256').update(bytes).digest('hex')}`
    })
  }

  for (const patchDirectory of new Set(declaredPatchPaths.map((patchPath) => dirname(patchPath)))) {
    const declaredInDirectory = declaredPatchPaths.filter((patchPath) => dirname(patchPath) === patchDirectory)
    const actualInDirectory = readdirSync(resolve(repositoryRoot, patchDirectory), { withFileTypes: true })
      .filter((entry) => entry.isFile() && entry.name.endsWith('.patch'))
      .map((entry) => `${patchDirectory}/${entry.name}`)
      .sort()
    if (JSON.stringify(declaredInDirectory) !== JSON.stringify(actualInDirectory)) {
      errors.push(`${provenancePath}:patchSeries must list every patch in ${patchDirectory} in lexical order.`)
    }
  }

  patchSeries[provenance.componentId] = {
    digest: `sha256:${seriesHash.digest('hex')}`,
    patches
  }
}

if (errors.length > 0) {
  for (const error of errors) console.error(`error: ${error}`)
  process.exitCode = 1
} else {
  const derived = {
    frontendDependencies: {
      ...(packageLockRoot.dependencies ?? {}),
      ...(packageLockRoot.devDependencies ?? {})
    },
    patchSeries
  }
  if (jsonOutput) console.log(JSON.stringify(derived, null, 2))
  else {
    console.log(
      `Maintained inputs valid: ${Object.keys(derived.frontendDependencies).length} direct frontend dependencies and ${Object.values(patchSeries).reduce((count, series) => count + series.patches.length, 0)} source patches.`
    )
  }
}
