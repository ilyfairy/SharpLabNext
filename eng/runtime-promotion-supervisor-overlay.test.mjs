import assert from 'node:assert/strict'
import childProcess from 'node:child_process'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import {
  produceRuntimePromotionSupervisorOverlay,
  runRuntimePromotionSupervisorOverlay,
  RuntimePromotionSupervisorOverlayError,
} from './runtime-promotion-supervisor-overlay.mjs'
import {
  runtimePromotionPlanSignaturePath,
  serializeRuntimePromotionPlan,
  signRuntimePromotionPlan,
} from './runtime-promotion-plan-signature.mjs'

const profileId = 'wine-dotnet-7-linux-x64'
const imageId = `sha256:${'a'.repeat(64)}`
const pinnedReference = `registry.example/runtime@sha256:${'b'.repeat(64)}`
const sourceRevision = 'c'.repeat(40)
const controlImages = Object.freeze({
  'artifact-store': {
    reference: `registry.example/artifact-store@sha256:${'d'.repeat(64)}`,
    imageId: `sha256:${'e'.repeat(64)}`,
  },
  'worker-artifacts-default': {
    reference: `registry.example/worker-artifacts-default@sha256:${'f'.repeat(64)}`,
    imageId: `sha256:${'1'.repeat(64)}`,
  },
  'runtime-supervisor': {
    reference: `registry.example/runtime-supervisor@sha256:${'2'.repeat(64)}`,
    imageId: `sha256:${'3'.repeat(64)}`,
  },
})
const planPath = `profiles/runtime-promotion-plans/${profileId}.json`
const profilePath = `profiles/runtime-promotion-plans/${profileId}.profile.json`
const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const planKeys = crypto.generateKeyPairSync('ed25519')
const planKeyId = `sha256:${crypto.createHash('sha256').update(
  planKeys.publicKey.export({ type: 'spki', format: 'der' }),
).digest('hex')}`

function sha256(bytes) {
  return `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
}

function writeJson(root, relativePath, value, serialize = undefined) {
  const filename = path.join(root, ...relativePath.split('/'))
  fs.mkdirSync(path.dirname(filename), { recursive: true })
  fs.writeFileSync(filename, serialize?.(value) ?? `${JSON.stringify(value, null, 2)}\n`)
  return filename
}

function createFixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-promotion-overlay-'))
  fs.mkdirSync(path.join(root, '.git'))
  const profile = {
    schemaVersion: 1,
    id: profileId,
    image: pinnedReference,
    runtimeImageId: imageId,
    capabilities: ['run', 'jit-asm'],
    allowedSecurityPolicyIds: ['runtime-job-default'],
    securityPolicies: [{ id: 'runtime-job-default' }],
  }
  const profileFilename = writeJson(root, profilePath, profile)
  const plan = {
    schemaVersion: 1,
    profileId,
    sourceRevision,
    producer: {
      id: 'sharplabnext-runtime-preflight-v1',
      sourceRevision,
    },
    preflightProfile: {
      path: profilePath,
      sha256: sha256(fs.readFileSync(profileFilename)),
    },
    image: {
      reference: pinnedReference,
      imageId,
      sizeBytes: 512,
    },
  }
  const writeSignedPlan = value => {
    const bytes = serializeRuntimePromotionPlan(value)
    fs.writeFileSync(path.join(root, ...planPath.split('/')), bytes)
    fs.writeFileSync(
      path.join(root, ...runtimePromotionPlanSignaturePath(profileId).split('/')),
      `${signRuntimePromotionPlan(bytes, planKeys.privateKey)}\n`,
    )
  }
  writeSignedPlan(plan)
  const planFilename = path.join(root, ...planPath.split('/'))
  const outputFilename = path.join(
    root,
    '.tmp',
    'runtime-promotion-preflight',
    `${profileId}.compose.json`,
  )
  t.after(() => fs.rmSync(root, { recursive: true, force: true }))
  return {
    root,
    profile,
    plan,
    profileFilename,
    planFilename,
    outputFilename,
    writeProfile(value, bind = true) {
      writeJson(root, profilePath, value)
      if (bind) {
        plan.preflightProfile.sha256 = sha256(fs.readFileSync(profileFilename))
        writeSignedPlan(plan)
      }
    },
    writePlan(value = plan) {
      writeSignedPlan(value)
    },
  }
}

function produce(fixture, overrides = {}, options = {}) {
  return produceRuntimePromotionSupervisorOverlay({
    planPath,
    supervisorPort: '18082',
    artifactStorePort: '18081',
    artifactWorkerPort: '18083',
    runtimeSupervisorImage: controlImages['runtime-supervisor'].reference,
    artifactStoreImage: controlImages['artifact-store'].reference,
    artifactWorkerImage: controlImages['worker-artifacts-default'].reference,
    ...overrides,
  }, {
    repositoryRoot: fixture.root,
    planSignaturePublicKey: planKeys.publicKey,
    planSignatureKeyId: planKeyId,
    inspectImage(reference) {
      const image = Object.values(controlImages).find(item => item.reference === reference)
      assert.ok(image, `unexpected control image ${reference}`)
      return {
        imageId: image.imageId,
        sizeBytes: 512,
        operatingSystem: 'linux',
        architecture: 'amd64',
        repoDigests: [reference],
        labels: {
          'org.opencontainers.image.revision': sourceRevision,
          'io.sharplabnext.source.revision': sourceRevision,
        },
      }
    },
    ...options,
  })
}

test('writes only the minimal plan-bound loopback preflight overlay', t => {
  const fixture = createFixture(t)
  const result = produce(fixture)

  assert.equal(result.profileId, profileId)
  assert.equal(result.relativeOutputPath, `.tmp/runtime-promotion-preflight/${profileId}.compose.json`)
  assert.equal(result.outputPath, fixture.outputFilename)
  assert.equal(result.planSha256, sha256(fs.readFileSync(fixture.planFilename)))
  assert.equal(result.profileSha256, sha256(fs.readFileSync(fixture.profileFilename)))
  assert.deepEqual(JSON.parse(fs.readFileSync(fixture.outputFilename, 'utf8')), result.overlay)

  assert.deepEqual(Object.keys(result.overlay).sort(), ['networks', 'services'])
  assert.deepEqual(result.overlay.networks, {
    'runtime-promotion-preflight': {
      driver: 'bridge',
      internal: false,
      driver_opts: {
        'com.docker.network.bridge.enable_ip_masquerade': 'false',
      },
    },
  })
  assert.deepEqual(Object.keys(result.overlay.services), [
    'artifact-store',
    'worker-artifacts-default',
    'runtime-supervisor',
  ])
  assert.deepEqual(result.overlay.services['artifact-store'], {
    image: controlImages['artifact-store'].reference,
    pull_policy: 'never',
    networks: ['control', 'runtime-promotion-preflight'],
    ports: [{
      name: 'promotion-preflight-artifacts',
      target: 8080,
      published: '18081',
      host_ip: '127.0.0.1',
      protocol: 'tcp',
    }],
  })
  const artifactWorker = result.overlay.services['worker-artifacts-default']
  assert.deepEqual(artifactWorker, {
    image: controlImages['worker-artifacts-default'].reference,
    pull_policy: 'never',
    networks: ['control', 'runtime-promotion-preflight'],
    depends_on: {
      'artifact-store': { condition: 'service_started' },
    },
    environment: {
      ArtifactWorker__WorkerImageId: controlImages['worker-artifacts-default'].imageId,
    },
    ports: [{
      name: 'promotion-preflight-artifact-worker',
      target: 8080,
      published: '18083',
      host_ip: '127.0.0.1',
      protocol: 'tcp',
    }],
  })
  const supervisor = result.overlay.services['runtime-supervisor']
  assert.deepEqual(Object.keys(supervisor).sort(), [
    'depends_on',
    'environment',
    'image',
    'networks',
    'ports',
    'pull_policy',
    'restart',
    'volumes',
  ])
  assert.equal(supervisor.restart, 'no')
  assert.equal(supervisor.image, controlImages['runtime-supervisor'].reference)
  assert.equal(supervisor.pull_policy, 'never')
  assert.deepEqual(supervisor.networks, ['control', 'runtime-promotion-preflight'])
  assert.deepEqual(supervisor.depends_on, {
    'artifact-store': { condition: 'service_started' },
    'worker-artifacts-default': { condition: 'service_started' },
  })
  assert.deepEqual(supervisor.ports, [{
    name: 'promotion-preflight-supervisor',
    target: 8080,
    published: '18082',
    host_ip: '127.0.0.1',
    protocol: 'tcp',
  }])
  assert.deepEqual(supervisor.environment, {
    RuntimePromotionPreflight__Enabled: 'true',
    RuntimePromotionPreflight__PlanSha256: result.planSha256,
    RuntimePromotionPreflight__SourceRevision: sourceRevision,
    RuntimePromotionPreflight__ProfilePath:
      '/run/sharplabnext-preflight/runtime-profile.json',
    RuntimePromotionPreflight__ProfileSha256: result.profileSha256,
    RuntimePromotionPreflight__MeasurementHelperImage:
      controlImages['runtime-supervisor'].reference,
    RuntimePromotionPreflight__MeasurementHelperImageId:
      controlImages['runtime-supervisor'].imageId,
    RuntimeSupervisor__SessionReuseEnabled: 'false',
  })
  assert.deepEqual(supervisor.volumes, [{
    type: 'bind',
    source: fixture.profileFilename,
    target: '/run/sharplabnext-preflight/runtime-profile.json',
    read_only: true,
    bind: { create_host_path: false },
  }])

  for (const forbidden of ['profiles', 'security_policies', 'secrets']) {
    assert.equal(Object.hasOwn(supervisor, forbidden), false, forbidden)
    assert.equal(Object.hasOwn(artifactWorker, forbidden), false, forbidden)
  }
  assert.equal(
    Object.keys(supervisor.environment).some(key =>
      /^(?:RuntimeSupervisor__(?:Profiles|SecurityPolicies)__|InternalServiceAuth__)/.test(key)),
    false,
  )
  assert.equal(JSON.stringify(result.overlay).includes('/var/run/docker.sock'), false)
})

test('binds the measurement helper to a distinct runtime-supervisor image', t => {
  {
    const fixture = createFixture(t)
    assert.throws(
      () => produce(fixture, { runtimeSupervisorImage: pinnedReference }),
      /measurement helper image must be distinct from the candidate runtime image/,
    )
  }
  {
    const fixture = createFixture(t)
    const wrongRepository = `registry.example/not-the-supervisor@sha256:${'4'.repeat(64)}`
    assert.throws(() => produce(fixture, { runtimeSupervisorImage: wrongRepository }, {
      inspectImage(reference) {
        const image = Object.values(controlImages).find(item => item.reference === reference)
        return {
          imageId: image?.imageId ?? `sha256:${'5'.repeat(64)}`,
          sizeBytes: 512,
          operatingSystem: 'linux',
          architecture: 'amd64',
          repoDigests: [reference],
          labels: {
            'org.opencontainers.image.revision': sourceRevision,
            'io.sharplabnext.source.revision': sourceRevision,
          },
        }
      },
    }), /repository must be named runtime-supervisor/)
  }
  {
    const fixture = createFixture(t)
    assert.throws(() => produce(fixture, {}, {
      inspectImage(reference) {
        const image = Object.values(controlImages).find(item => item.reference === reference)
        assert.ok(image)
        return {
          imageId: reference === controlImages['runtime-supervisor'].reference
            ? imageId
            : image.imageId,
          sizeBytes: 512,
          operatingSystem: 'linux',
          architecture: 'amd64',
          repoDigests: [reference],
          labels: {
            'org.opencontainers.image.revision': sourceRevision,
            'io.sharplabnext.source.revision': sourceRevision,
          },
        }
      },
    }), /measurement helper image must be distinct from the candidate runtime image/)
  }
})

test('requires distinct canonical unprivileged ports', t => {
  const fixture = createFixture(t)
  for (const invalid of ['0', '1023', '65536', '01024', '1e4', ' 18082']) {
    assert.throws(
      () => produce(fixture, { supervisorPort: invalid }),
      /Supervisor port must be an integer from 1024 to 65535/,
      invalid,
    )
  }
  assert.throws(
    () => produce(fixture, { supervisorPort: '18081', artifactStorePort: '18081' }),
    /ports must be different/,
  )
  assert.throws(
    () => produce(fixture, { artifactWorkerPort: '18082' }),
    /ports must be different/,
  )
  assert.throws(
    () => produce(fixture, { artifactWorkerPort: '1023' }),
    /Artifact worker port must be an integer from 1024 to 65535/,
  )
  const result = produce(fixture, {
    supervisorPort: '1024',
    artifactStorePort: '65535',
    artifactWorkerPort: '1025',
  })
  assert.equal(result.overlay.services['runtime-supervisor'].ports[0].published, '1024')
  assert.equal(result.overlay.services['artifact-store'].ports[0].published, '65535')
  assert.equal(result.overlay.services['worker-artifacts-default'].ports[0].published, '1025')
})

test('rejects a wrong profile digest, mutable image, image mismatch, and receipt', t => {
  {
    const fixture = createFixture(t)
    fixture.plan.preflightProfile.sha256 = `sha256:${'0'.repeat(64)}`
    fixture.writePlan()
    assert.throws(() => produce(fixture), /preflightProfile sha256 must equal/)
  }
  {
    const fixture = createFixture(t)
    fixture.plan.image.reference = 'registry.example/runtime:mutable'
    fixture.profile.image = fixture.plan.image.reference
    fixture.writeProfile(fixture.profile)
    assert.throws(() => produce(fixture), /Promotion plan image identity is invalid/)
  }
  {
    const fixture = createFixture(t)
    fixture.profile.image = `registry.example/other@sha256:${'c'.repeat(64)}`
    fixture.writeProfile(fixture.profile)
    assert.throws(() => produce(fixture), /preflight profile image must equal/i)
  }
  {
    const fixture = createFixture(t)
    fixture.profile.promotionReceipt = {
      path: `profiles/runtime-promotion-receipts/${profileId}.json`,
      sha256: `sha256:${'d'.repeat(64)}`,
    }
    fixture.writeProfile(fixture.profile)
    assert.throws(() => produce(fixture), /cannot contain a promotion receipt/i)
  }
})

test('accepts only the canonical repository-relative plan path', t => {
  const fixture = createFixture(t)
  for (const invalid of [
    path.resolve(fixture.planFilename),
    planPath.replaceAll('/', '\\'),
    `./${planPath}`,
    `profiles/runtime-promotion-plans/../${profileId}.json`,
    `profiles/runtime-promotion-plans/${profileId}.profile.json`,
  ]) {
    assert.throws(
      () => produce(fixture, { planPath: invalid }),
      RuntimePromotionSupervisorOverlayError,
      invalid,
    )
  }
})

test('input drift leaves the previously installed overlay byte-for-byte intact', t => {
  const fixture = createFixture(t)
  produce(fixture)
  const original = fs.readFileSync(fixture.outputFilename)

  assert.throws(() => produce(fixture, { supervisorPort: '19082' }, {
    beforeCommit() {
      fs.appendFileSync(fixture.planFilename, ' ')
    },
  }), /Promotion plan or profile changed before commit/)
  assert.deepEqual(fs.readFileSync(fixture.outputFilename), original)
  assert.deepEqual(
    fs.readdirSync(path.dirname(fixture.outputFilename)),
    [`${profileId}.compose.json`],
  )
})

test('rejects linked output components and non-regular existing output', t => {
  {
    const fixture = createFixture(t)
    fs.mkdirSync(path.dirname(fixture.outputFilename), { recursive: true })
    fs.mkdirSync(fixture.outputFilename)
    assert.throws(() => produce(fixture), /Overlay output must be a regular non-link file/)
  }

  const fixture = createFixture(t)
  const outside = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-overlay-outside-'))
  t.after(() => fs.rmSync(outside, { recursive: true, force: true }))
  fs.mkdirSync(path.join(fixture.root, '.tmp'))
  try {
    fs.symlinkSync(outside, path.join(fixture.root, '.tmp', 'runtime-promotion-preflight'),
      process.platform === 'win32' ? 'junction' : 'dir')
  } catch (error) {
    if (error?.code === 'EPERM') {
      t.diagnostic('Current Windows policy does not permit creating a directory link.')
      return
    }
    throw error
  }
  assert.throws(
    () => produce(fixture),
    /non-link directory|linked or reparse-point path component/,
  )
  assert.deepEqual(fs.readdirSync(outside), [])
})

test('help and invalid CLI invocations do not create output', t => {
  const fixture = createFixture(t)
  const messages = []
  const output = {
    log: message => messages.push(['log', message]),
    error: message => messages.push(['error', message]),
  }

  assert.equal(runRuntimePromotionSupervisorOverlay(['--help'], {
    repositoryRoot: fixture.root,
    output,
  }), 0)
  assert.equal(runRuntimePromotionSupervisorOverlay([], {
    repositoryRoot: fixture.root,
    output,
  }), 1)
  assert.equal(fs.existsSync(path.join(fixture.root, '.tmp')), false)
  assert.equal(messages.some(([, message]) => message.includes('Usage:')), true)
  assert.equal(messages.some(([kind, message]) =>
    kind === 'error' && message.includes('Missing required planPath')), true)
})

test('generated overlay merges with the production Compose model', t => {
  const composeVersion = childProcess.spawnSync('docker', ['compose', 'version'], {
    encoding: 'utf8',
    windowsHide: true,
  })
  if (composeVersion.error?.code === 'ENOENT' || composeVersion.status !== 0) {
    t.skip('Docker Compose is not available on this host.')
    return
  }
  const fixture = createFixture(t)
  produce(fixture)
  const result = childProcess.spawnSync('docker', [
    'compose',
    '--project-directory', repositoryRoot,
    '-f', path.join(repositoryRoot, 'deploy', 'compose.prod.yaml'),
    '-f', fixture.outputFilename,
    'config',
    '--quiet',
  ], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    windowsHide: true,
  })
  assert.equal(
    result.status,
    0,
    `docker compose config failed:\n${result.stdout}\n${result.stderr}`,
  )
})
