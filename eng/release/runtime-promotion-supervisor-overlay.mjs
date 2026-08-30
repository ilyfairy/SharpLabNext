/**
 * Produces the minimal Compose overlay for one trusted runtime promotion preflight.
 */

import crypto from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath, pathToFileURL } from 'node:url'

import { parseOwnedJson } from './strict-owned-json.mjs'
import {
  inspectDockerImage,
  validateRuntimeImageInspection,
} from './runtime-promotion-image-binding.mjs'
import {
  runtimePromotionPlanSignaturePath,
  serializeRuntimePromotionPlan,
  verifyRuntimePromotionPlanSignature,
} from './runtime-promotion-plan-signature.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const maximumInputBytes = 1024 * 1024
const idPattern = /^[a-z0-9][a-z0-9._-]{0,127}$/
const sha256Pattern = /^sha256:[0-9a-f]{64}$/
const pinnedReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/
const gitCommitPattern = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/
const profileMountPath = '/run/sharplabnext-preflight/runtime-profile.json'
// Docker does not publish ports for containers attached only to an
// `internal: true` network. Keep the control network for service-to-service
// traffic and add this project-scoped bridge solely for host-side evidence
// requests. Disabling masquerading prevents that attachment from becoming a
// general outbound network path.
const preflightNetworkName = 'runtime-promotion-preflight'

export const runtimePromotionSupervisorOverlayUsage = `Usage:
  node eng/release/runtime-promotion-supervisor-overlay.mjs \\
    --plan profiles/runtime-promotion-plans/<profile-id>.json \\
    --supervisor-port <1024..65535> \\
    --artifact-store-port <1024..65535> \\
    --artifact-worker-port <1024..65535> \\
    --runtime-supervisor-image <repository@sha256:...> \\
    --artifact-store-image <repository@sha256:...> \\
    --artifact-worker-image <repository@sha256:...>`

export class RuntimePromotionSupervisorOverlayError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'RuntimePromotionSupervisorOverlayError'
  }
}

function sha256(bytes) { return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`; }

function readRegularFile(filename, label) {
  let before
  try {
    before = fs.lstatSync(filename)
  } catch (error) {
    throw new RuntimePromotionSupervisorOverlayError(`Could not read ${label}: ${error.message}`, {
      cause: error,
    })
  }
  if (!before.isFile() || before.isSymbolicLink() ||
      before.size < 1 || before.size > maximumInputBytes) {
    throw new RuntimePromotionSupervisorOverlayError(`${label} must be a 1..${maximumInputBytes} byte regular non-link file.`)
  }
  const descriptor = fs.openSync(
    filename,
    fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0),
  )
  try {
    const opened = fs.fstatSync(descriptor)
    if (!opened.isFile() || opened.size !== before.size ||
        (opened.dev !== undefined && opened.dev !== before.dev) ||
        (opened.ino !== undefined && opened.ino !== before.ino)) {
      throw new RuntimePromotionSupervisorOverlayError(`${label} changed while opening.`)
    }
    const bytes = fs.readFileSync(descriptor)
    const after = fs.fstatSync(descriptor)
    if (bytes.length !== opened.size || after.size !== opened.size ||
        after.mtimeMs !== opened.mtimeMs || after.ctimeMs !== opened.ctimeMs ||
        (opened.dev !== undefined && after.dev !== opened.dev) ||
        (opened.ino !== undefined && after.ino !== opened.ino)) {
      throw new RuntimePromotionSupervisorOverlayError(`${label} changed while reading.`)
    }
    return bytes
  } finally {
    fs.closeSync(descriptor)
  }
}

function parseJson(bytes, label) {
  const failures = []
  const value = parseOwnedJson(bytes, label, failures)
  if (failures.length > 0 || value === undefined || value === null ||
      typeof value !== 'object' || Array.isArray(value)) {
    throw new RuntimePromotionSupervisorOverlayError(
      failures.join(' ') || `${label} root must be an object.`,
    )
  }
  return value
}

function requireEqual(actual, expected, label) {
  if (actual !== expected) {
    throw new RuntimePromotionSupervisorOverlayError(`${label} must equal ${JSON.stringify(expected)}; observed ${JSON.stringify(actual)}.`)
  }
}

function requireExactKeys(value, expected, label) {
  if (value === null || typeof value !== 'object' || Array.isArray(value)) {
    throw new RuntimePromotionSupervisorOverlayError(`${label} must be an object.`)
  }
  const actual = Object.keys(value).sort()
  const canonical = [...expected].sort()
  if (JSON.stringify(actual) !== JSON.stringify(canonical)) {
    throw new RuntimePromotionSupervisorOverlayError(`${label} must contain exactly [${canonical.join(', ')}].`)
  }
}

function parsePort(value, label) {
  if (!/^[0-9]{4,5}$/.test(value ?? '')) {
    throw new RuntimePromotionSupervisorOverlayError(`${label} must be an integer from 1024 to 65535.`)
  }
  const port = Number(value)
  if (!Number.isSafeInteger(port) || port < 1024 || port > 65535 || String(port) !== value) {
    throw new RuntimePromotionSupervisorOverlayError(`${label} must be an integer from 1024 to 65535.`)
  }
  return port
}

function validateRepositoryRoot(root) {
  const absolute = path.resolve(root)
  const info = fs.lstatSync(absolute)
  if (!info.isDirectory() || info.isSymbolicLink()) {
    throw new RuntimePromotionSupervisorOverlayError('Repository root must be a non-link directory.')
  }
  const gitMarker = path.join(absolute, '.git')
  if (!fs.existsSync(gitMarker)) {
    throw new RuntimePromotionSupervisorOverlayError('Repository root has no .git marker.')
  }
  const gitInfo = fs.lstatSync(gitMarker)
  if (gitInfo.isSymbolicLink()) {
    throw new RuntimePromotionSupervisorOverlayError('Repository .git marker cannot be a link.')
  }
  return fs.realpathSync(absolute)
}

function pathsEqual(left, right) {
  const normalize = value => process.platform === 'win32'
    ? path.resolve(value).toLowerCase()
    : path.resolve(value)
  return normalize(left) === normalize(right)
}

function inspectOutputDirectory(directory, label) {
  let info
  try {
    info = fs.lstatSync(directory)
  } catch (error) {
    throw new RuntimePromotionSupervisorOverlayError(`Could not inspect ${label}: ${error.message}`, {
      cause: error,
    })
  }
  if (!info.isDirectory() || info.isSymbolicLink()) {
    throw new RuntimePromotionSupervisorOverlayError(`${label} must be a non-link directory.`)
  }
  const realPath = fs.realpathSync(directory)
  if (!pathsEqual(realPath, directory)) {
    throw new RuntimePromotionSupervisorOverlayError(
      `${label} cannot contain a linked or reparse-point path component.`,
    )
  }
  return Object.freeze({
    path: directory,
    realPath,
    dev: info.dev,
    ino: info.ino,
  })
}

function createOutputDirectoryState(root) {
  const components = []
  let current = root
  for (const [segment, label] of [
    ['.tmp', 'runtime promotion temporary directory'],
    ['runtime-promotion-preflight', 'runtime promotion preflight output directory'],
  ]) {
    current = path.join(current, segment)
    try {
      fs.mkdirSync(current, { mode: 0o700 })
    } catch (error) {
      if (error?.code !== 'EEXIST') {
        throw new RuntimePromotionSupervisorOverlayError(
          `Could not create ${label}: ${error.message}`,
          { cause: error },
        )
      }
    }
    components.push(inspectOutputDirectory(current, label))
  }
  return Object.freeze({
    root,
    directory: current,
    components: Object.freeze(components),
  })
}

function verifyOutputDirectoryState(state) {
  for (const expected of state.components) {
    const observed = inspectOutputDirectory(expected.path, 'runtime promotion output directory')
    if (!pathsEqual(observed.realPath, expected.realPath) ||
        observed.dev !== expected.dev || observed.ino !== expected.ino) {
      throw new RuntimePromotionSupervisorOverlayError('Runtime promotion output directory identity changed during commit.')
    }
  }
}

function canonicalPlanBinding(root, planPath) {
  if (typeof planPath !== 'string' || planPath.includes('\\') || path.isAbsolute(planPath)) {
    throw new RuntimePromotionSupervisorOverlayError('Promotion plan path must be canonical and relative.')
  }
  const match = /^profiles\/runtime-promotion-plans\/([a-z0-9][a-z0-9._-]{0,127})\.json$/.exec(
    planPath,
  )
  if (match === null || match[1].endsWith('.profile')) {
    throw new RuntimePromotionSupervisorOverlayError('Promotion plan path must be profiles/runtime-promotion-plans/<profile-id>.json.')
  }
  const profileId = match[1]
  const absolute = path.join(root, ...planPath.split('/'))
  return { profileId, absolute }
}

function validatePlanAndProfile(plan, planBytes, profile, profileBytes, binding) {
  requireEqual(plan.schemaVersion, 1, 'promotion plan schemaVersion')
  requireEqual(plan.profileId, binding.profileId, 'promotion plan profileId')
  if (!idPattern.test(plan.profileId ?? '')) {
    throw new RuntimePromotionSupervisorOverlayError('Promotion plan profileId is invalid.')
  }
  if (!gitCommitPattern.test(plan.sourceRevision ?? '')) {
    throw new RuntimePromotionSupervisorOverlayError('Promotion plan sourceRevision must be a full lowercase Git commit.')
  }
  requireExactKeys(plan.producer, ['id', 'sourceRevision'], 'promotion plan producer')
  requireEqual(plan.producer.id, 'sharplabnext-runtime-preflight-v1', 'promotion plan producer id')
  requireEqual(
    plan.producer.sourceRevision,
    plan.sourceRevision,
    'promotion plan producer sourceRevision',
  )
  requireExactKeys(plan.preflightProfile, ['path', 'sha256'], 'preflightProfile binding')
  requireEqual(
    plan.preflightProfile.path,
    `profiles/runtime-promotion-plans/${binding.profileId}.profile.json`,
    'preflightProfile path',
  )
  if (!sha256Pattern.test(plan.preflightProfile.sha256 ?? '')) {
    throw new RuntimePromotionSupervisorOverlayError('preflightProfile sha256 is invalid.')
  }
  requireEqual(plan.preflightProfile.sha256, sha256(profileBytes), 'preflightProfile sha256')
  requireExactKeys(plan.image, ['reference', 'imageId', 'sizeBytes'], 'promotion plan image')
  if (!pinnedReferencePattern.test(plan.image.reference ?? '') ||
      !sha256Pattern.test(plan.image.imageId ?? '') ||
      !Number.isSafeInteger(plan.image.sizeBytes) || plan.image.sizeBytes <= 0) {
    throw new RuntimePromotionSupervisorOverlayError('Promotion plan image identity is invalid.')
  }

  requireEqual(profile.schemaVersion, 1, 'preflight profile schemaVersion')
  requireEqual(profile.id, binding.profileId, 'preflight profile id')
  requireEqual(profile.image, plan.image.reference, 'preflight profile image')
  requireEqual(profile.runtimeImageId, plan.image.imageId, 'preflight profile runtimeImageId')
  if (profile.promotionReceipt !== undefined) {
    throw new RuntimePromotionSupervisorOverlayError('Immutable preflight profile cannot contain a promotion receipt.')
  }
  if (!Array.isArray(profile.capabilities) || !profile.capabilities.includes('run') ||
      profile.capabilities.length === 0 || new Set(profile.capabilities).size !== profile.capabilities.length) {
    throw new RuntimePromotionSupervisorOverlayError('Immutable preflight profile capabilities are invalid.')
  }
  if (!Array.isArray(profile.allowedSecurityPolicyIds) ||
      profile.allowedSecurityPolicyIds.length !== 1 ||
      !Array.isArray(profile.securityPolicies) || profile.securityPolicies.length !== 1 ||
      profile.securityPolicies[0]?.id !== profile.allowedSecurityPolicyIds[0]) {
    throw new RuntimePromotionSupervisorOverlayError('Immutable preflight profile must embed exactly its one allowed security policy.')
  }
  return {
    planSha256: sha256(planBytes),
    profileSha256: sha256(profileBytes),
    sourceRevision: plan.sourceRevision,
  }
}

function imageRepositoryName(reference) {
  const separator = reference.lastIndexOf('@sha256:')
  if (separator <= 0) return undefined
  const repository = reference.slice(0, separator)
  return repository.slice(repository.lastIndexOf('/') + 1)
}

function bindControlImages(input, sourceRevision, candidateImage, inspectImage, inspectOptions) {
  const definitions = [
    ['artifact-store', input.artifactStoreImage],
    ['worker-artifacts-default', input.artifactWorkerImage],
    ['runtime-supervisor', input.runtimeSupervisorImage],
  ]
  const bindings = {}
  for (const [service, reference] of definitions) {
    if (!pinnedReferencePattern.test(reference ?? '')) {
      throw new RuntimePromotionSupervisorOverlayError(
        `${service} control image must be repository@sha256:<64 lowercase hex>.`,
      )
    }
    if (service === 'runtime-supervisor' && reference === candidateImage.reference) {
      throw new RuntimePromotionSupervisorOverlayError('Runtime measurement helper image must be distinct from the candidate runtime image.')
    }
    let inspection
    try {
      inspection = inspectImage(reference, inspectOptions)
    } catch (error) {
      throw new RuntimePromotionSupervisorOverlayError(
        `Could not bind ${service} control image '${reference}': ${error.message}`,
        { cause: error },
      )
    }
    const failures = validateRuntimeImageInspection(inspection, {
      sourceRevision,
      pinnedReference: reference,
    })
    if (failures.length > 0) {
      throw new RuntimePromotionSupervisorOverlayError(
        `${service} control image binding failed:\n- ${failures.join('\n- ')}`,
      )
    }
    bindings[service] = Object.freeze({
      reference,
      imageId: inspection.imageId,
      sizeBytes: inspection.sizeBytes,
    })
  }
  if (new Set(Object.values(bindings).map(binding => binding.reference)).size !== definitions.length ||
      new Set(Object.values(bindings).map(binding => binding.imageId)).size !== definitions.length) {
    throw new RuntimePromotionSupervisorOverlayError('Runtime promotion control services must use three distinct immutable images.')
  }
  const measurementHelper = bindings['runtime-supervisor']
  if (imageRepositoryName(measurementHelper.reference) !== 'runtime-supervisor') {
    throw new RuntimePromotionSupervisorOverlayError('Runtime measurement helper image repository must be named runtime-supervisor.')
  }
  if (measurementHelper.reference === candidateImage.reference ||
      measurementHelper.imageId === candidateImage.imageId) {
    throw new RuntimePromotionSupervisorOverlayError('Runtime measurement helper image must be distinct from the candidate runtime image.')
  }
  return Object.freeze(bindings)
}

function createOverlay(
  profileAbsolutePath,
  digests,
  controlImages,
  supervisorPort,
  artifactStorePort,
  artifactWorkerPort,
) {
  return {
    networks: {
      [preflightNetworkName]: {
        driver: 'bridge',
        internal: false,
        driver_opts: {
          'com.docker.network.bridge.enable_ip_masquerade': 'false',
        },
      },
    },
    services: {
      'artifact-store': {
        image: controlImages['artifact-store'].reference,
        pull_policy: 'never',
        networks: ['control', preflightNetworkName],
        ports: [{
          name: 'promotion-preflight-artifacts',
          target: 8080,
          published: String(artifactStorePort),
          host_ip: '127.0.0.1',
          protocol: 'tcp',
        }],
      },
      'worker-artifacts-default': {
        image: controlImages['worker-artifacts-default'].reference,
        pull_policy: 'never',
        networks: ['control', preflightNetworkName],
        depends_on: {
          'artifact-store': { condition: 'service_started' },
        },
        environment: {
          ArtifactWorker__WorkerImageId: controlImages['worker-artifacts-default'].imageId,
        },
        ports: [{
          name: 'promotion-preflight-artifact-worker',
          target: 8080,
          published: String(artifactWorkerPort),
          host_ip: '127.0.0.1',
          protocol: 'tcp',
        }],
      },
      'runtime-supervisor': {
        image: controlImages['runtime-supervisor'].reference,
        pull_policy: 'never',
        restart: 'no',
        networks: ['control', preflightNetworkName],
        depends_on: {
          'artifact-store': { condition: 'service_started' },
          'worker-artifacts-default': { condition: 'service_started' },
        },
        ports: [{
          name: 'promotion-preflight-supervisor',
          target: 8080,
          published: String(supervisorPort),
          host_ip: '127.0.0.1',
          protocol: 'tcp',
        }],
        environment: {
          RuntimePromotionPreflight__Enabled: 'true',
          RuntimePromotionPreflight__PlanSha256: digests.planSha256,
          RuntimePromotionPreflight__SourceRevision: digests.sourceRevision,
          RuntimePromotionPreflight__ProfilePath: profileMountPath,
          RuntimePromotionPreflight__ProfileSha256: digests.profileSha256,
          RuntimePromotionPreflight__MeasurementHelperImage:
            controlImages['runtime-supervisor'].reference,
          RuntimePromotionPreflight__MeasurementHelperImageId:
            controlImages['runtime-supervisor'].imageId,
          RuntimeSupervisor__SessionReuseEnabled: 'false',
        },
        volumes: [{
          type: 'bind',
          source: profileAbsolutePath,
          target: profileMountPath,
          read_only: true,
          bind: { create_host_path: false },
        }],
      },
    },
  }
}

function snapshotOutput(filename) {
  if (!fs.existsSync(filename)) return { exists: false }
  const info = fs.lstatSync(filename)
  if (!info.isFile() || info.isSymbolicLink()) {
    throw new RuntimePromotionSupervisorOverlayError('Overlay output must be a regular non-link file.')
  }
  const bytes = readRegularFile(filename, 'existing overlay output')
  return { exists: true, bytes }
}

function outputUnchanged(filename, snapshot) {
  if (fs.existsSync(filename) !== snapshot.exists) return false
  if (!snapshot.exists) return true
  return readRegularFile(filename, 'existing overlay output').equals(snapshot.bytes)
}

function writeAtomic(filename, bytes, verifyInputs, directoryState) {
  const directory = directoryState.directory
  if (!pathsEqual(path.dirname(filename), directory)) {
    throw new RuntimePromotionSupervisorOverlayError('Overlay output escaped the runtime promotion preflight directory.')
  }
  verifyOutputDirectoryState(directoryState)
  const snapshot = snapshotOutput(filename)
  const temporary = path.join(directory, `.${path.basename(filename)}.${process.pid}.${crypto.randomUUID()}.tmp`)
  verifyOutputDirectoryState(directoryState)
  const descriptor = fs.openSync(temporary, 'wx', 0o600)
  try {
    fs.writeFileSync(descriptor, bytes)
    fs.fsyncSync(descriptor)
  } finally {
    fs.closeSync(descriptor)
  }
  let backup
  let installed = false
  try {
    verifyInputs()
    verifyOutputDirectoryState(directoryState)
    if (!outputUnchanged(filename, snapshot)) {
      throw new RuntimePromotionSupervisorOverlayError('Overlay output changed before commit.')
    }
    if (snapshot.exists) {
      backup = path.join(directory, `.${path.basename(filename)}.${process.pid}.${crypto.randomUUID()}.bak`)
      fs.renameSync(filename, backup)
    }
    fs.renameSync(temporary, filename)
    installed = true
    verifyInputs()
    verifyOutputDirectoryState(directoryState)
    if (!readRegularFile(filename, 'written overlay output').equals(bytes)) {
      throw new RuntimePromotionSupervisorOverlayError('Overlay output changed after commit.')
    }
    if (backup !== undefined) fs.rmSync(backup)
    backup = undefined
  } catch (error) {
    const rollbackFailures = []
    try {
      verifyOutputDirectoryState(directoryState)
      if (installed && fs.existsSync(filename)) fs.rmSync(filename)
      if (backup !== undefined && fs.existsSync(backup) && !fs.existsSync(filename)) {
        fs.renameSync(backup, filename)
        backup = undefined
      }
    } catch (rollbackError) {
      rollbackFailures.push(rollbackError)
    }
    if (rollbackFailures.length > 0) {
      throw new RuntimePromotionSupervisorOverlayError(
        `Overlay commit failed and could not be rolled back; any backup remains at ${backup ?? 'its original path'}.`,
        { cause: new AggregateError([error, ...rollbackFailures]) },
      )
    }
    throw error
  } finally {
    try {
      verifyOutputDirectoryState(directoryState)
      fs.rmSync(temporary, { force: true })
    } catch {
      // Never follow a replaced output directory merely to clean a temporary file.
    }
  }
}

export function produceRuntimePromotionSupervisorOverlay(input, options = {}) {
  const root = validateRepositoryRoot(options.repositoryRoot ?? repositoryRoot)
  const supervisorPort = parsePort(String(input.supervisorPort ?? ''), 'Supervisor port')
  const artifactStorePort = parsePort(String(input.artifactStorePort ?? ''), 'Artifact Store port')
  const artifactWorkerPort = parsePort(String(input.artifactWorkerPort ?? ''), 'Artifact worker port')
  if (new Set([supervisorPort, artifactStorePort, artifactWorkerPort]).size !== 3) {
    throw new RuntimePromotionSupervisorOverlayError('Supervisor, Artifact Store, and artifact worker ports must be different.')
  }
  const binding = canonicalPlanBinding(root, input.planPath)
  const planBytes = readRegularFile(binding.absolute, 'runtime promotion plan')
  const plan = parseJson(planBytes, 'runtime promotion plan')
  if (!planBytes.equals(serializeRuntimePromotionPlan(plan))) {
    throw new RuntimePromotionSupervisorOverlayError('Runtime promotion plan is not canonical.')
  }
  const signaturePath = path.join(root, ...runtimePromotionPlanSignaturePath(binding.profileId).split('/'))
  const signatureBytes = readRegularFile(signaturePath, 'runtime promotion plan signature')
  try { verifyRuntimePromotionPlanSignature(planBytes, signatureBytes,
    options.planSignaturePublicKey === undefined
      ? {}
      : { publicKey: options.planSignaturePublicKey, keyId: options.planSignatureKeyId }) } catch (error) {
    throw new RuntimePromotionSupervisorOverlayError(`Runtime promotion plan signature is invalid: ${error.message}`, { cause: error })
  }
  const expectedProfilePath =
    `profiles/runtime-promotion-plans/${binding.profileId}.profile.json`
  const profileAbsolutePath = path.join(root, ...expectedProfilePath.split('/'))
  const profileBytes = readRegularFile(profileAbsolutePath, 'immutable preflight profile')
  const profile = parseJson(profileBytes, 'immutable preflight profile')
  const digests = validatePlanAndProfile(plan, planBytes, profile, profileBytes, binding)
  const inspectImage = options.inspectImage ?? inspectDockerImage
  const inspectOptions = {
    cwd: root,
    env: options.env ?? process.env,
  }
  const controlImages = bindControlImages(
    input,
    digests.sourceRevision,
    plan.image,
    inspectImage,
    inspectOptions,
  )
  const overlay = createOverlay(
    profileAbsolutePath,
    digests,
    controlImages,
    supervisorPort,
    artifactStorePort,
    artifactWorkerPort,
  )
  const bytes = Buffer.from(`${JSON.stringify(overlay, null, 2)}\n`, 'utf8')
  const relativeOutputPath = `.tmp/runtime-promotion-preflight/${binding.profileId}.compose.json`
  const outputDirectoryState = createOutputDirectoryState(root)
  const outputPath = path.join(outputDirectoryState.directory, `${binding.profileId}.compose.json`)
  const verifyInputs = () => {
    if (!readRegularFile(binding.absolute, 'runtime promotion plan').equals(planBytes) ||
        !readRegularFile(signaturePath, 'runtime promotion plan signature').equals(signatureBytes) ||
        !readRegularFile(profileAbsolutePath, 'immutable preflight profile').equals(profileBytes)) {
      throw new RuntimePromotionSupervisorOverlayError('Promotion plan or profile changed before commit.')
    }
    const repeatedControlImages = bindControlImages(
      input,
      digests.sourceRevision,
      plan.image,
      inspectImage,
      inspectOptions,
    )
    if (JSON.stringify(repeatedControlImages) !== JSON.stringify(controlImages)) {
      throw new RuntimePromotionSupervisorOverlayError('Runtime promotion control image bindings changed before commit.')
    }
  }
  options.beforeCommit?.()
  writeAtomic(outputPath, bytes, verifyInputs, outputDirectoryState)
  return Object.freeze({
    profileId: binding.profileId,
    planSha256: digests.planSha256,
    profileSha256: digests.profileSha256,
    sourceRevision: digests.sourceRevision,
    controlImages,
    outputPath,
    relativeOutputPath,
    overlay: Object.freeze(overlay),
  })
}

function parseArguments(argv) {
  const parsed = {}
  const fields = new Map([
    ['--plan', 'planPath'],
    ['--supervisor-port', 'supervisorPort'],
    ['--artifact-store-port', 'artifactStorePort'],
    ['--artifact-worker-port', 'artifactWorkerPort'],
    ['--runtime-supervisor-image', 'runtimeSupervisorImage'],
    ['--artifact-store-image', 'artifactStoreImage'],
    ['--artifact-worker-image', 'artifactWorkerImage'],
  ])
  for (let index = 0; index < argv.length; index += 2) {
    const name = argv[index]
    const field = fields.get(name)
    const value = argv[index + 1]
    if (field === undefined || value === undefined || value.length === 0 || parsed[field] !== undefined) {
      throw new RuntimePromotionSupervisorOverlayError(`Invalid or duplicate overlay option '${name}'.`)
    }
    parsed[field] = value
  }
  for (const field of fields.values()) {
    if (parsed[field] === undefined) {
      throw new RuntimePromotionSupervisorOverlayError(`Missing required ${field}.`)
    }
  }
  return parsed
}

export function runRuntimePromotionSupervisorOverlay(argv, options = {}) {
  const output = options.output ?? console
  if (argv.length === 1 && (argv[0] === '--help' || argv[0] === '-h')) {
    output.log(runtimePromotionSupervisorOverlayUsage)
    return 0
  }
  try {
    const result = produceRuntimePromotionSupervisorOverlay(parseArguments(argv), options)
    output.log(`Wrote ${result.outputPath}; plan ${result.planSha256}; profile ${result.profileSha256}.`)
    return 0
  } catch (error) {
    output.error(`runtime promotion Supervisor overlay error: ${error.message}`)
    output.error(runtimePromotionSupervisorOverlayUsage)
    return error instanceof RuntimePromotionSupervisorOverlayError ? 1 : 2
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = runRuntimePromotionSupervisorOverlay(process.argv.slice(2))
}
