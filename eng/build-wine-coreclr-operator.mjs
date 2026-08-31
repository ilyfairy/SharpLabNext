/**
 * Build the clean Wine CoreCLR userspace operator from an exact Git revision.
 *
 * This is intentionally separate from runtime candidates: it creates their
 * common, operator-only input. Formal builds never use working-tree bytes.
 */

import { spawnSync } from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import {
  isDigestPinnedImageReference,
  isGitCommitIdentity,
  validateWineCoreClrUserspaceInputs,
} from './runtime-candidate-input-validation.mjs'
import {
  bindRuntimeCandidateImage,
  inspectDockerImage,
  inspectGitSourceState,
  validateGitSourceState,
} from './release/runtime-promotion-image-binding.mjs'
import {
  wineCoreClrOperatorExpectedLabels,
} from './build-runtime-candidate.mjs'
import {
  createCommittedSourceContext,
} from './committed-source-context.mjs'
import {
  wineCoreClrUserspaceEnvironment,
} from './runtime-wine-userspace-lock.mjs'
import {
  createWineCoreClrOperatorReceipt,
  serializeWineCoreClrOperatorReceipt,
  signWineCoreClrOperatorReceipt,
  verifyWineCoreClrOperatorReceipt,
  wineCoreClrOperatorCommittedFiles,
  wineCoreClrOperatorReceiptPublicKeyPath,
  writeWineCoreClrOperatorReceiptAtomically,
} from './release/wine-coreclr-operator-receipt.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const target = 'operator-wine-coreclr'
const sourceIdentityModeEnvironmentVariable = 'SHARPLABNEXT_SOURCE_IDENTITY_MODE'
const contentSourceIdentityMode = 'content'
const sourceContextInput = 'OPERATOR_SOURCE_CONTEXT'
const promotionEligibilityInput = 'OPERATOR_PROMOTION_ELIGIBLE'
const publishDestinationInput = 'WINE_CORECLR_OPERATOR_PUBLISH_DESTINATION'
const receiptPathInput = 'WINE_CORECLR_OPERATOR_RECEIPT_PATH'
const signingKeyInput = 'WINE_CORECLR_OPERATOR_SIGNING_KEY_PATH'

function fail(message) { throw new Error(message); }

function isNonBuildInvocation(arguments_) {
  return arguments_.some((argument, index) =>
    argument === '--print' ||
    argument === '--check' ||
    argument === '--call' ||
    argument.startsWith('--call=') ||
    (index > 0 && arguments_[index - 1] === '--call'))
}

function validateAdditionalArguments(arguments_) {
  const booleanOptions = new Set(['--check', '--load', '--no-cache', '--print', '--pull'])
  const valueOptions = new Set(['--allow', '--builder', '--call', '--metadata-file', '--progress', '--provenance', '--sbom'])
  for (let index = 0; index < arguments_.length; index++) {
    const argument = arguments_[index]
    if (argument === '-f' || /^-f(?:=|[^-])/.test(argument) ||
        argument === '--file' || argument.startsWith('--file=')) {
      fail('Wine operator builds cannot override the reviewed Bake file')
    }
    if (argument === '--set' || argument.startsWith('--set=')) {
      fail('Wine operator builds cannot override validated target fields with --set')
    }
    if (argument === '--push' || argument.startsWith('--push=')) {
      fail('Wine operator builds must remain local until their image labels are verified')
    }
    if (booleanOptions.has(argument)) continue
    const equals = argument.indexOf('=')
    const name = equals < 0 ? argument : argument.slice(0, equals)
    if (!valueOptions.has(name)) fail(`unsupported Wine operator Bake option '${argument}'`)
    if (equals >= 0) continue
    index++
    if (index >= arguments_.length || arguments_[index].length === 0) {
      fail(`${name} requires a value`)
    }
  }
}

function isSafeBuildTagPart(value) {
  return typeof value === 'string' && value.length > 0 &&
    !/[\s\\"'\r\n]/.test(value)
}

export function wineCoreClrOperatorImageTag(values) { return `${values.IMAGE_PREFIX}/operator-wine-coreclr:${values.RELEASE_ID}`; }

export function validateWineCoreClrOperatorBuildInputs(values, sourceRoot = repositoryRoot) {
  const failures = []
  for (const name of ['IMAGE_PREFIX', 'RELEASE_ID']) {
    if (!isSafeBuildTagPart(values?.[name])) failures.push(`${name} must be a non-empty safe image tag component`)
  }
  if (!/^[0-9]+$/.test(values?.SOURCE_DATE_EPOCH ?? '')) {
    failures.push('SOURCE_DATE_EPOCH must be Unix seconds')
  }
  if (!isGitCommitIdentity(values?.SOURCE_REVISION)) {
    failures.push('SOURCE_REVISION must be a full lowercase Git commit identity')
  }
  if (!isDigestPinnedImageReference(values?.BASE_DOTNET_RUNTIME_DEPS_IMAGE)) {
    failures.push('BASE_DOTNET_RUNTIME_DEPS_IMAGE must be a repository@sha256:<64 lowercase hex> reference')
  }
  try {
    const locked = wineCoreClrUserspaceEnvironment(values, sourceRoot)
    failures.push(...validateWineCoreClrUserspaceInputs({ ...values, ...locked }))
  } catch (error) { failures.push(error.message) }
  return Object.freeze(failures)
}

export function createWineCoreClrOperatorBakeArguments(additionalArguments = [], sourceRoot = undefined) {
  validateAdditionalArguments(additionalArguments)
  const nonBuild = isNonBuildInvocation(additionalArguments)
  const outputArguments = nonBuild || additionalArguments.includes('--load')
    ? additionalArguments
    : ['--load', ...additionalArguments]
  const context = sourceRoot === undefined ? [] : ['--set', `${target}.context=${sourceRoot}`]
  return [
    'buildx', 'bake', '--file',
    sourceRoot === undefined ? 'eng/bake.hcl' : path.join(sourceRoot, 'eng', 'bake.hcl'),
    ...context,
    ...outputArguments,
    target,
  ]
}

function committedSourceFiles() { return [...wineCoreClrOperatorCommittedFiles]; }

function sourceEnvironment(values, binding) {
  return {
    ...values,
    [sourceContextInput]: binding.promotionEligible ? 'committed' : 'working-tree-content',
    [promotionEligibilityInput]: String(binding.promotionEligible),
  }
}

function publicationConfiguration(values) {
  const fields = [publishDestinationInput, receiptPathInput, signingKeyInput]
  const supplied = fields.filter(field => typeof values?.[field] === 'string' && values[field].length > 0)
  if (supplied.length !== 0 && supplied.length !== fields.length) {
    throw new Error(`${fields.join(', ')} must be supplied together for formal sign/publish mode`)
  }
  if (supplied.length === 0) return undefined
  if (!/^[^@\s]+:[a-z0-9][a-z0-9._-]{0,127}$/.test(values[publishDestinationInput])) {
    throw new Error(`${publishDestinationInput} must be a canonical immutable-release tagged registry reference`)
  }
  if (!path.isAbsolute(values[receiptPathInput]) || !path.isAbsolute(values[signingKeyInput])) {
    throw new Error(`${receiptPathInput} and ${signingKeyInput} must be absolute paths`)
  }
  if (path.resolve(values[receiptPathInput]) === path.resolve(values[signingKeyInput]) ||
      path.resolve(`${values[receiptPathInput]}.sig`) === path.resolve(values[signingKeyInput])) {
    throw new Error('Wine operator receipt output must not overwrite its signing key')
  }
  return Object.freeze({
    destination: values[publishDestinationInput], receiptPath: values[receiptPathInput], signingKeyPath: values[signingKeyInput],
  })
}

function validatePublicationSigningKey(configuration, publicKey) {
  const stat = fs.lstatSync(configuration.signingKeyPath)
  if (!stat.isFile() || stat.isSymbolicLink() || stat.size <= 0 || stat.size > 16 * 1024) {
    throw new Error(`${signingKeyInput} must be a bounded regular non-link Ed25519 private key`)
  }
  const privateKey = crypto.createPrivateKey(fs.readFileSync(configuration.signingKeyPath))
  if (privateKey.asymmetricKeyType !== 'ed25519') {
    throw new Error(`${signingKeyInput} must contain an Ed25519 private key`)
  }
  const challenge = Buffer.from('SharpLabNext Wine operator receipt key binding v1\n', 'utf8')
  const signature = crypto.sign(null, challenge, privateKey)
  if (!crypto.verify(null, challenge, publicKey, signature)) {
    throw new Error(`${signingKeyInput} does not match the committed Wine operator receipt public key`)
  }
}

function committedFileDigests(root) {
  return Object.freeze(Object.fromEntries(committedSourceFiles().sort().map(relative => [
    relative,
    `sha256:${crypto.createHash('sha256').update(fs.readFileSync(path.join(root, relative))).digest('hex')}`,
  ])))
}

function runDocker(spawn, arguments_, cwd, env, description) {
  const result = spawn('docker', arguments_, { cwd, env, shell: false, stdio: 'ignore' })
  if (result?.error !== undefined || result?.status !== 0) {
    throw new Error(`${description}: ${result?.error?.message ?? result?.stderr ?? `exit ${result?.status}`}`)
  }
}

function sourceTree(spawn, root, revision, env) {
  const result = spawn('git', ['rev-parse', `${revision}^{tree}`], { cwd: root, env, encoding: 'utf8', shell: false })
  const tree = String(result?.stdout ?? '').trim()
  if (result?.status !== 0 || !/^(?:[0-9a-f]{40}|[0-9a-f]{64})$/.test(tree)) throw new Error('could not resolve committed source tree')
  return tree
}

function publishAndReceipt(configuration, image, values, expectedLabels, source, root, spawn, env, publicKey) {
  runDocker(spawn, ['tag', wineCoreClrOperatorImageTag(values), configuration.destination], root, env, 'could not tag verified Wine operator')
  runDocker(spawn, ['push', configuration.destination], root, env, 'could not publish verified Wine operator')
  const tagged = inspectDockerImage(configuration.destination, { spawn, cwd: root, env })
  const repository = configuration.destination.slice(0, configuration.destination.lastIndexOf(':'))
  const digests = tagged.repoDigests.filter(value => value.startsWith(`${repository}@sha256:`))
  if (digests.length !== 1) throw new Error('published Wine operator must expose exactly one destination RepoDigest')
  const pinned = bindRuntimeCandidateImage({
    candidateReference: configuration.destination, pinnedReference: digests[0], sourceRevision: values.SOURCE_REVISION,
    expectedLabels, inspect: reference => inspectDockerImage(reference, { spawn, cwd: root, env }),
  })
  if (pinned.imageId !== image.imageId || pinned.sizeBytes !== image.sizeBytes) throw new Error('published Wine operator changed image identity after local verification')
  const receipt = createWineCoreClrOperatorReceipt({
    source,
    operator: {
      reference: digests[0], imageId: pinned.imageId, sizeBytes: pinned.sizeBytes,
      platform: `${pinned.operatingSystem}/${pinned.architecture}`,
      userspace: { version: values.WINE_CORECLR_USERSPACE_VERSION, digest: values.WINE_CORECLR_USERSPACE_DIGEST, sourceUri: values.WINE_CORECLR_USERSPACE_SOURCE_URI },
      baseImage: values.BASE_DOTNET_RUNTIME_DEPS_IMAGE, labels: Object.fromEntries(Object.entries(pinned.labels).sort()),
    },
  })
  const signature = signWineCoreClrOperatorReceipt(receipt, fs.readFileSync(configuration.signingKeyPath))
  verifyWineCoreClrOperatorReceipt(
    serializeWineCoreClrOperatorReceipt(receipt),
    signature,
    { publicKey },
  )
  const written = writeWineCoreClrOperatorReceiptAtomically(configuration.receiptPath, receipt, signature)
  verifyWineCoreClrOperatorReceipt(
    fs.readFileSync(written.receiptPath),
    fs.readFileSync(written.signaturePath),
    { publicKey },
  )
  return written
}

export function runWineCoreClrOperatorBuild(argv, values = process.env, spawn = spawnSync, output = console, testHooks = {}) {
  const effectiveRepositoryRoot = path.resolve(testHooks.repositoryRoot ?? repositoryRoot)
  const contentSourceIdentity = String(values?.[sourceIdentityModeEnvironmentVariable] ?? '').toLowerCase() === contentSourceIdentityMode
  const additionalArguments = argv
  try { validateAdditionalArguments(additionalArguments) } catch (error) {
    output.error(`Wine operator input error: ${error.message}`)
    return 64
  }

  const failures = validateWineCoreClrOperatorBuildInputs(values, effectiveRepositoryRoot)
  if (failures.length > 0) {
    for (const failure of failures) output.error(`Wine operator input error: ${failure}`)
    return 1
  }

  const nonBuild = isNonBuildInvocation(additionalArguments)
  let publication
  try { publication = publicationConfiguration(values) } catch (error) {
    output.error(`Wine operator input error: ${error.message}`)
    return 1
  }
  if (publication !== undefined &&
      (nonBuild || contentSourceIdentity)) {
    output.error('Wine operator input error: content-source or non-build invocations may not sign or publish')
    return 1
  }
  const receiptPublicKey = testHooks.operatorReceiptPublicKey ??
    fs.readFileSync(wineCoreClrOperatorReceiptPublicKeyPath)
  if (publication !== undefined) {
    try { validatePublicationSigningKey(publication, receiptPublicKey) } catch (error) {
      output.error(`Wine operator input error: ${error.message}`)
      return 1
    }
  }
  let dockerEnvironment = { ...values }
  delete dockerEnvironment.BUILDX_BAKE_FILE
  delete dockerEnvironment.BUILDX_BAKE_FILE_SEPARATOR
  delete dockerEnvironment[sourceContextInput]
  delete dockerEnvironment[promotionEligibilityInput]

  let binding = { promotionEligible: true }
  let sourceContext
  if (!nonBuild) {
    try {
      const before = inspectGitSourceState({
        spawn,
        cwd: effectiveRepositoryRoot,
        env: dockerEnvironment,
        allowedDirtyPaths: testHooks.allowedDirtyPaths ?? [],
        fallbackRevision: values.SOURCE_REVISION,
      })
      binding = validateGitSourceState(before, values.SOURCE_REVISION, { promotionMode: !contentSourceIdentity })
      if (binding.failures.length > 0) {
        for (const failure of binding.failures) output.error(`Wine operator source error: ${failure}`)
        return 1
      }
      if (binding.promotionEligible) {
        const createContext = testHooks.createCommittedSourceContext ?? createCommittedSourceContext
        sourceContext = createContext({
          repositoryRoot: effectiveRepositoryRoot,
          revision: values.SOURCE_REVISION,
          requiredFiles: committedSourceFiles(),
          spawn,
        })
      } else {
        output.log('Content source identity selects local working-tree bytes; this Wine operator is not promotion-eligible.')
      }
    } catch (error) {
      output.error(`Wine operator source error: ${error.message}`)
      return 1
    }
  }

  const cleanup = () => {
    if (sourceContext === undefined) return true
    try { sourceContext.dispose(); sourceContext = undefined; return true } catch (error) {
      output.error(`Wine operator source error: ${error.message}`)
      sourceContext = undefined
      return false
    }
  }
  const sourceRoot = sourceContext?.directory ?? effectiveRepositoryRoot
  try {
    dockerEnvironment = { ...dockerEnvironment, ...wineCoreClrUserspaceEnvironment(values, sourceRoot) }
  } catch (error) {
    cleanup()
    output.error(`Wine operator input error: ${error.message}`)
    return 1
  }
  // Bake still evaluates required provenance variables for --print/--check.
  // Supplying the formal values here only makes the graph inspectable; no
  // image is produced and every real build still passes through Git binding.
  const buildEnvironment = sourceEnvironment(dockerEnvironment, binding)
  let bakeArguments
  try {
    bakeArguments = createWineCoreClrOperatorBakeArguments(
      additionalArguments,
      nonBuild ? undefined : sourceRoot,
    )
  } catch (error) {
    cleanup()
    output.error(`Wine operator input error: ${error.message}`)
    return 64
  }

  let result
  try {
    result = spawn('docker', bakeArguments, {
      cwd: sourceRoot,
      env: buildEnvironment,
      stdio: testHooks.buildStdio ?? 'inherit',
      shell: false,
    })
  } catch (error) { result = { error } }

  let sourceChanged = false
  if (!nonBuild) {
    try {
      let after = validateGitSourceState(inspectGitSourceState({
        spawn,
        cwd: effectiveRepositoryRoot,
        env: dockerEnvironment,
        allowedDirtyPaths: testHooks.allowedDirtyPaths ?? [],
        fallbackRevision: values.SOURCE_REVISION,
      }), values.SOURCE_REVISION, { promotionMode: !contentSourceIdentity })
      if (after.failures.length > 0 || after.promotionEligible !== binding.promotionEligible) {
        for (const failure of after.failures) output.error(`Wine operator source error: ${failure}`)
        if (after.failures.length === 0) output.error('Wine operator source error: Git source state changed during Bake')
        sourceChanged = true
      }
    } catch (error) {
      output.error(`Wine operator source error: ${error.message}`)
      sourceChanged = true
    }
  }
  let receiptSource
  if (!nonBuild && publication !== undefined && binding.promotionEligible &&
      !sourceChanged && result?.error === undefined && result?.status === 0) {
    try {
      receiptSource = Object.freeze({
        revision: buildEnvironment.SOURCE_REVISION,
        tree: sourceTree(
          spawn,
          effectiveRepositoryRoot,
          buildEnvironment.SOURCE_REVISION,
          dockerEnvironment,
        ),
        files: committedFileDigests(sourceRoot),
      })
    } catch (error) {
      output.error(`Wine operator source error: ${error.message}`)
      sourceChanged = true
    }
  }
  const cleaned = cleanup()
  if (sourceChanged || !cleaned) return 1
  if (result.error !== undefined) {
    output.error(`Could not start docker: ${result.error.message}`)
    return 1
  }
  if (result.status !== 0 || nonBuild) return result.status ?? 1

  try {
    const expectedLabels = wineCoreClrOperatorExpectedLabels(buildEnvironment, {
      context: buildEnvironment[sourceContextInput],
      promotionEligible: binding.promotionEligible,
    })
    const image = bindRuntimeCandidateImage({
      candidateReference: wineCoreClrOperatorImageTag(values),
      sourceRevision: values.SOURCE_REVISION,
      expectedLabels,
      inspect: reference => inspectDockerImage(reference, {
        spawn,
        cwd: effectiveRepositoryRoot,
        env: dockerEnvironment,
      }),
    })
    output.log(`Verified Wine CoreCLR operator ${image.imageId} from ${image.reference ?? wineCoreClrOperatorImageTag(values)}.`)
    if (publication !== undefined) {
      if (receiptSource === undefined) {
        throw new Error('formal Wine operator publication has no immutable committed source evidence')
      }
      const receipt = publishAndReceipt(
        publication,
        image,
        buildEnvironment,
        expectedLabels,
        receiptSource,
        effectiveRepositoryRoot,
        spawn,
        dockerEnvironment,
        receiptPublicKey,
      )
      output.log(`Published and signed Wine CoreCLR operator receipt ${receipt.receiptPath}.`)
    }
    return 0
  } catch (error) {
    output.error(`Wine operator identity error: ${error.message}`)
    return 1
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runWineCoreClrOperatorBuild(process.argv.slice(2))
}
