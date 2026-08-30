import type { ArtifactProcessorManifest, CatalogDocument, CompatibilityRuleKind, LanguageManifest, OutputManifest, ReferenceSetManifest, RuntimeManifest, ToolchainManifest } from '../api/types'
import { languageForWorkbench } from './languageDefaults'

export type MobilePane = 'code' | 'result'

export interface SelectionIntent {
  languageId: string
  toolchainId: string | null
  referenceSetId: string | null
  outputId: string
  runtimeId: string | null
}

/** Hidden catalog entries are retained for operators and migrations, but are
 * never eligible for ordinary workbench selection or capability discovery. */
function isVisible(item: unknown): boolean {
  if (typeof item !== 'object' || item === null || !('visibility' in item)) return true
  return item.visibility !== 'hidden'
}

function compareVersionLabelsDescending(left: string, right: string): number {
  const versionPart = (label: string) => (label.includes('/') ? label.slice(0, label.indexOf('/')) : label)
  const leftParts = versionPart(left).match(/\d+/g)?.map(Number) ?? []
  const rightParts = versionPart(right).match(/\d+/g)?.map(Number) ?? []
  const length = Math.max(leftParts.length, rightParts.length)
  for (let index = 0; index < length; index += 1) {
    const difference = (rightParts[index] ?? 0) - (leftParts[index] ?? 0)
    if (difference !== 0) return difference
  }
  return left.localeCompare(right)
}

export const fallbackLanguage: LanguageManifest = {
  id: 'csharp',
  displayName: 'C#',
  monacoLanguageId: 'csharp',
  extensions: ['.cs'],
  defaultFileName: 'Program.cs',
  defaultSource: 'using System;\n\nConsole.WriteLine("Hello from SharpLabNext");\n',
  defaultToolchainId: 'roslyn-main',
  capabilities: [],
  legacyAliases: ['cs'],
}

function isRuleKind(kind: CompatibilityRuleKind, expected: CompatibilityRuleKind): boolean {
  return kind === expected
}

function containsAll(available: readonly string[] | undefined, required: readonly string[] | undefined): boolean {
  const values = new Set(available ?? [])
  return (required ?? []).every((value) => values.has(value))
}

function requiredMetadataTags(toolchain: ToolchainManifest, referenceSet: ReferenceSetManifest): string[] {
  return [...new Set([...toolchain.metadataFeatureTags, ...referenceSet.metadataFeatureTags])]
}

function hasProcessorCompatibility(catalog: CatalogDocument, artifactFormat: string, processorId: string, metadataFeatureTags: readonly string[]): boolean {
  return catalog.compatibility.some((rule) => isRuleKind(rule.kind, 'artifact-processor') && rule.allowed && rule.fromId === artifactFormat && rule.toId === processorId && containsAll(metadataFeatureTags, rule.requiredMetadataFeatureTags))
}

function reachableArtifactFormats(catalog: CatalogDocument, toolchain: ToolchainManifest, referenceSet: ReferenceSetManifest): Set<string> {
  const metadataFeatureTags = requiredMetadataTags(toolchain, referenceSet)
  const reachable = new Set(toolchain.producesArtifactFormats)
  const pending = [...reachable]

  while (pending.length > 0) {
    const inputFormat = pending.shift()
    if (!inputFormat) continue

    for (const processor of catalog.artifactProcessors) {
      if (!processor.acceptsArtifactFormats.includes(inputFormat) || !containsAll(processor.acceptedMetadataFeatureTags, metadataFeatureTags) || !hasProcessorCompatibility(catalog, inputFormat, processor.id, metadataFeatureTags)) {
        continue
      }

      for (const transformation of processor.transformations ?? []) {
        if (transformation.inputArtifactFormat !== inputFormat || reachable.has(transformation.outputArtifactFormat)) {
          continue
        }
        reachable.add(transformation.outputArtifactFormat)
        pending.push(transformation.outputArtifactFormat)
      }
    }
  }

  return reachable
}

export function languageById(catalog: CatalogDocument, languageId: string): LanguageManifest | undefined {
  const language = catalog.languages.find((candidate) => candidate.id === languageId)
  return language ? languageForWorkbench(language) : undefined
}

export function toolchainById(catalog: CatalogDocument, toolchainId: string | null): ToolchainManifest | undefined {
  if (toolchainId === null) return undefined
  return catalog.toolchains.find((toolchain) => toolchain.id === toolchainId)
}

export function referenceSetById(catalog: CatalogDocument, referenceSetId: string | null): ReferenceSetManifest | undefined {
  if (referenceSetId === null) return undefined
  return catalog.referenceSets.find((referenceSet) => referenceSet.id === referenceSetId && isVisible(referenceSet))
}

export function referenceSetDisplayName(referenceSet: ReferenceSetManifest): string {
  const displayName = referenceSet.displayName
  if (displayName.startsWith('.NET Framework ')) {
    return displayName.replace(/^\.NET Framework v?/, '.NET Framework v')
  }
  if (!/^\d/.test(displayName)) return displayName
  if (referenceSet.targetFramework.startsWith('netcoreapp')) return `.NET Core ${displayName}`
  if (referenceSet.runtimeFamily.includes('netfx')) return `.NET Framework v${displayName}`
  if (referenceSet.targetFramework.startsWith('net')) return `.NET ${displayName}`
  return displayName
}

export function outputById(catalog: CatalogDocument, outputId: string): OutputManifest | undefined {
  return catalog.outputs.find((output) => output.id === outputId)
}

export function runtimeById(catalog: CatalogDocument, runtimeId: string | null): RuntimeManifest | undefined {
  if (runtimeId === null) return undefined
  return catalog.runtimes.find((runtime) => runtime.id === runtimeId && isVisible(runtime))
}

export function toolchainOptionsFor(catalog: CatalogDocument, languageId: string): ToolchainManifest[] {
  return catalog.toolchains.filter((toolchain) => toolchain.supportedLanguageIds.includes(languageId) && isVisible(toolchain))
}

export function referenceSetOptionsFor(catalog: CatalogDocument, toolchainId: string | null): ReferenceSetManifest[] {
  const toolchain = toolchainById(catalog, toolchainId)
  if (!toolchain) return []

  return catalog.referenceSets
    .filter(
      (referenceSet) =>
        isVisible(referenceSet) &&
        toolchain.allowedReferenceSetIds.includes(referenceSet.id) &&
        catalog.compatibility.some((rule) => isRuleKind(rule.kind, 'toolchain-reference-set') && rule.allowed && rule.fromId === toolchain.id && rule.toId === referenceSet.id),
    )
    .sort((left, right) => {
      const versionOrder = compareVersionLabelsDescending(left.displayName, right.displayName)
      return versionOrder !== 0 ? versionOrder : left.id.localeCompare(right.id)
    })
}

function needsArtifactProcessor(catalog: CatalogDocument, output: OutputManifest): boolean {
  return catalog.artifactProcessors.some((processor) => processor.capabilities.includes(output.id))
}

function findOutputProcessor(catalog: CatalogDocument, toolchain: ToolchainManifest, referenceSet: ReferenceSetManifest, output: OutputManifest, reachableFormats: ReadonlySet<string>, requiredArtifactFormat?: string) {
  const metadataFeatureTags = requiredMetadataTags(toolchain, referenceSet)
  return catalog.artifactProcessors.find(
    (processor) =>
      processor.capabilities.includes(output.id) &&
      containsAll(processor.acceptedMetadataFeatureTags, metadataFeatureTags) &&
      processor.acceptsArtifactFormats.some(
        (artifactFormat) =>
          (requiredArtifactFormat === undefined || artifactFormat === requiredArtifactFormat) &&
          reachableFormats.has(artifactFormat) &&
          (output.acceptedArtifactFormats.length === 0 || output.acceptedArtifactFormats.includes(artifactFormat)) &&
          hasProcessorCompatibility(catalog, artifactFormat, processor.id, metadataFeatureTags),
      ),
  )
}

function runtimeCapabilitiesFor(output: OutputManifest): string[] {
  return output.requiredCapabilities.filter((capability) => ['run', 'jit-asm', 'execution-flow', 'inspection'].includes(capability))
}

interface ReferenceFramework {
  name: string
  version: number[]
}

function parseFrameworkVersion(value: string | undefined): number[] | null {
  if (!value) return null
  const numericPart = value.split(/[-+]/, 1)[0]
  if (!numericPart || !/^\d+(?:\.\d+){0,3}$/.test(numericPart)) return null
  return numericPart.split('.').map(Number)
}

function referenceFrameworkFor(referenceSet: ReferenceSetManifest): ReferenceFramework | null {
  const targetFramework = referenceSet.targetFramework
  let name: string
  let version: string

  if (targetFramework.startsWith('netcoreapp')) {
    name = 'Microsoft.NETCore.App'
    version = targetFramework.slice('netcoreapp'.length)
  } else if (targetFramework.startsWith('netframework')) {
    name = '.NETFramework'
    version = targetFramework.slice('netframework'.length)
  } else if (targetFramework.startsWith('net')) {
    name = referenceSet.runtimeFamily === 'netfx-clr-wine' ? '.NETFramework' : 'Microsoft.NETCore.App'
    version = targetFramework.slice(3)
    if (name === '.NETFramework' && !version.includes('.')) version = [...version].join('.')
  } else {
    return null
  }

  const parsed = parseFrameworkVersion(version)
  return parsed ? { name, version: parsed } : null
}

function versionMatchesTargetFramework(version: number[], target: number[]): boolean {
  return version.length >= target.length && target.every((part, index) => version[index] === part)
}

function compareAtTargetFrameworkPrecision(target: number[], bound: number[]): number {
  if (bound.length < target.length) return -1
  for (let index = 0; index < target.length; index += 1) {
    const difference = (target[index] ?? 0) - (bound[index] ?? 0)
    if (difference !== 0) return difference
  }
  return 0
}

function acceptsReferenceFramework(accepted: NonNullable<RuntimeManifest['acceptedFrameworks']>[number], reference: ReferenceFramework): boolean {
  if (accepted.name !== reference.name) return false

  const hasExactVersion = Boolean(accepted.exactVersion)
  const hasRange = Boolean(accepted.minimumVersion || accepted.maximumVersion)
  if (hasExactVersion === hasRange) return false
  if (hasExactVersion) {
    const exact = parseFrameworkVersion(accepted.exactVersion)
    return exact !== null && versionMatchesTargetFramework(exact, reference.version)
  }

  const minimum = parseFrameworkVersion(accepted.minimumVersion)
  const maximum = parseFrameworkVersion(accepted.maximumVersion)
  return minimum !== null && maximum !== null && compareAtTargetFrameworkPrecision(reference.version, minimum) >= 0 && compareAtTargetFrameworkPrecision(reference.version, maximum) <= 0
}

function hasCompatibleRuntimeContract(referenceSet: ReferenceSetManifest, runtime: RuntimeManifest): boolean {
  if (runtime.acceptedRuntimeFamilies?.length && !runtime.acceptedRuntimeFamilies.includes(referenceSet.runtimeFamily)) {
    return false
  }

  const referenceFramework = referenceFrameworkFor(referenceSet)
  if (!referenceFramework) return !runtime.acceptedFrameworks?.length
  if (referenceSet.runtimeFamily === 'coreclr' && referenceFramework.name === 'Microsoft.NETCore.App' && (runtime.family === 'coreclr' || runtime.family === 'coreclr-wine')) {
    const runtimeVersion = parseFrameworkVersion(runtime.resolvedVersion)
    if (runtimeVersion !== null && compareAtTargetFrameworkPrecision(referenceFramework.version, runtimeVersion) <= 0) {
      return true
    }
  }
  if (runtime.acceptedFrameworks?.length) {
    return runtime.acceptedFrameworks.some((accepted) => acceptsReferenceFramework(accepted, referenceFramework))
  }

  const resolvedVersion = parseFrameworkVersion(runtime.resolvedVersion)
  return referenceSet.runtimeFamily !== 'coreclr' || runtime.family !== 'coreclr' || (resolvedVersion !== null && versionMatchesTargetFramework(resolvedVersion, referenceFramework.version))
}

function runtimeVersionSpecificity(referenceSet: ReferenceSetManifest, runtime: RuntimeManifest): number {
  const referenceFramework = referenceFrameworkFor(referenceSet)
  const runtimeVersion = parseFrameworkVersion(runtime.resolvedVersion)
  if (!referenceFramework || !runtimeVersion || !versionMatchesTargetFramework(runtimeVersion, referenceFramework.version)) {
    return Number.MAX_SAFE_INTEGER
  }

  return runtimeVersion.length - referenceFramework.version.length
}

function isRuntimeCompatible(catalog: CatalogDocument, toolchain: ToolchainManifest, referenceSet: ReferenceSetManifest, artifactFormat: string, runtime: RuntimeManifest, output?: OutputManifest): boolean {
  const metadataFeatureTags = requiredMetadataTags(toolchain, referenceSet)
  const hasEdge = catalog.compatibility.some(
    (rule) =>
      isRuleKind(rule.kind, 'artifact-runtime') &&
      rule.allowed &&
      rule.fromId === artifactFormat &&
      rule.toId === runtime.id &&
      containsAll(metadataFeatureTags, rule.requiredMetadataFeatureTags) &&
      containsAll(runtime.providedMetadataFeatureTags, rule.requiredMetadataFeatureTags),
  )

  return (
    hasEdge &&
    runtime.acceptedArtifactFormats.includes(artifactFormat) &&
    hasCompatibleRuntimeContract(referenceSet, runtime) &&
    containsAll(runtime.providedMetadataFeatureTags, toolchain.metadataFeatureTags) &&
    containsAll(runtime.providedRuntimeFeatureTags, referenceSet.requiredRuntimeFeatureTags) &&
    containsAll(runtime.providedMetadataFeatureTags, referenceSet.metadataFeatureTags) &&
    (!output || runtimeCapabilitiesFor(output).every((capability) => runtime.capabilities.includes(capability)))
  )
}

function canOfferOutput(catalog: CatalogDocument, language: LanguageManifest, toolchain: ToolchainManifest, referenceSet: ReferenceSetManifest, output: OutputManifest): boolean {
  if (output.requiredCapabilities.includes('explain') && (!language.capabilities.includes('explain') || !toolchain.capabilities.includes('explain'))) {
    return false
  }

  const reachableFormats = reachableArtifactFormats(catalog, toolchain, referenceSet)
  const processorRequired = needsArtifactProcessor(catalog, output)
  let processor: ArtifactProcessorManifest | undefined
  let runtime: RuntimeManifest | undefined
  let finalFormat = toolchain.producesArtifactFormats[0]
  if (output.requiresRuntime) {
    for (const candidate of catalog.runtimes.filter(isVisible)) {
      const artifactFormat = candidate.acceptedArtifactFormats.find(
        (format) =>
          reachableFormats.has(format) &&
          (output.acceptedArtifactFormats.length === 0 || output.acceptedArtifactFormats.includes(format)) &&
          isRuntimeCompatible(catalog, toolchain, referenceSet, format, candidate, output) &&
          (!processorRequired || findOutputProcessor(catalog, toolchain, referenceSet, output, reachableFormats, format) !== undefined),
      )
      if (!artifactFormat) continue
      runtime = candidate
      finalFormat = artifactFormat
      processor = processorRequired ? findOutputProcessor(catalog, toolchain, referenceSet, output, reachableFormats, artifactFormat) : undefined
      break
    }
    if (!runtime) return false
  } else if (processorRequired) {
    processor = findOutputProcessor(catalog, toolchain, referenceSet, output, reachableFormats)
    if (!processor) return false
    finalFormat = processor.acceptsArtifactFormats.find((format) => reachableFormats.has(format))
  }

  return output.requiredCapabilities.every(
    (capability) =>
      language.capabilities.includes(capability) ||
      toolchain.capabilities.includes(capability) ||
      processor?.capabilities.includes(capability) === true ||
      (capability === 'managed-pe' && finalFormat === 'dotnet-managed-pe-v1') ||
      runtime?.capabilities.includes(capability) === true,
  )
}

export function outputOptionsFor(catalog: CatalogDocument, languageId: string, toolchainId: string | null, referenceSetId?: string | null): OutputManifest[] {
  const language = languageById(catalog, languageId)
  const toolchain = toolchainById(catalog, toolchainId)
  const referenceSet = referenceSetById(catalog, referenceSetId === undefined ? (toolchain?.defaultReferenceSetId ?? null) : referenceSetId)
  if (!language || !toolchain || !referenceSet) return []
  return catalog.outputs.filter((output) => canOfferOutput(catalog, language, toolchain, referenceSet, output))
}

export function runtimeOptionsFor(catalog: CatalogDocument, toolchainId: string | null, referenceSetId: string | null, outputId?: string): RuntimeManifest[] {
  const toolchain = toolchainById(catalog, toolchainId)
  const referenceSet = referenceSetById(catalog, referenceSetId)
  const output = outputId ? outputById(catalog, outputId) : undefined
  if (!toolchain || !referenceSet) return []

  const reachableFormats = reachableArtifactFormats(catalog, toolchain, referenceSet)

  return catalog.runtimes
    .filter(
      (runtime) =>
        isVisible(runtime) &&
        runtime.acceptedArtifactFormats.some(
          (artifactFormat) =>
            reachableFormats.has(artifactFormat) &&
            (!output || output.acceptedArtifactFormats.length === 0 || output.acceptedArtifactFormats.includes(artifactFormat)) &&
            isRuntimeCompatible(catalog, toolchain, referenceSet, artifactFormat, runtime, output) &&
            (!output || !needsArtifactProcessor(catalog, output) || findOutputProcessor(catalog, toolchain, referenceSet, output, reachableFormats, artifactFormat) !== undefined),
        ),
    )
    .sort((left, right) => {
      const specificityOrder = runtimeVersionSpecificity(referenceSet, left) - runtimeVersionSpecificity(referenceSet, right)
      if (specificityOrder !== 0) return specificityOrder
      const leftFamilyOrder = left.family === referenceSet.runtimeFamily ? 0 : 1
      const rightFamilyOrder = right.family === referenceSet.runtimeFamily ? 0 : 1
      if (leftFamilyOrder !== rightFamilyOrder) return leftFamilyOrder - rightFamilyOrder
      const versionOrder = compareVersionLabelsDescending(left.displayName, right.displayName)
      return versionOrder !== 0 ? versionOrder : left.id.localeCompare(right.id)
    })
}

export function normalizeSelectionIntent(catalog: CatalogDocument, requested: SelectionIntent): SelectionIntent {
  const language = languageById(catalog, requested.languageId) ?? catalog.languages[0]
  if (!language) return requested

  const toolchains = toolchainOptionsFor(catalog, language.id)
  const toolchain = toolchains.find((candidate) => candidate.id === requested.toolchainId) ?? toolchains.find((candidate) => candidate.id === language.defaultToolchainId) ?? toolchains[0]
  if (!toolchain) return { ...requested, languageId: language.id }

  const referenceSets = referenceSetOptionsFor(catalog, toolchain.id)
  const referenceSet = referenceSets.find((candidate) => candidate.id === requested.referenceSetId) ?? referenceSets.find((candidate) => candidate.id === toolchain.defaultReferenceSetId) ?? referenceSets[0]

  const outputs = outputOptionsFor(catalog, language.id, toolchain.id, referenceSet?.id ?? null)
  const presetDefaultOutputId = catalog.presets.find((preset) => isVisible(preset) && preset.languageId === language.id && preset.toolchainId === toolchain.id && preset.referenceSetId === referenceSet?.id)?.defaultOutputId
  const output = outputs.find((candidate) => candidate.id === requested.outputId) ?? outputs.find((candidate) => candidate.id === presetDefaultOutputId) ?? outputs.find((candidate) => candidate.id === 'compile-check') ?? outputs[0]

  const runtimes = runtimeOptionsFor(catalog, toolchain.id, referenceSet?.id ?? null, output?.id)
  const runtime = output?.requiresRuntime ? (runtimes.find((candidate) => candidate.id === requested.runtimeId) ?? runtimes[0]) : undefined

  return {
    languageId: language.id,
    toolchainId: toolchain.id,
    referenceSetId: referenceSet?.id ?? null,
    outputId: output?.id ?? requested.outputId,
    runtimeId: runtime?.id ?? null,
  }
}

export function availabilityLabel(health: string): string {
  if (health === 'healthy') return ''
  if (health === 'skeleton') return ' (development)'
  return ' (unavailable)'
}
