import assert from 'node:assert/strict'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import {
  createFrameworkSeedBuildSpec,
  createOperatorImageBuildSpec,
  frameworkSeedDefinitions,
} from '../image-build-inputs.mjs'
import { readPrerequisiteManifest } from '../prerequisite-cache.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const wineImage = `localhost:5000/wine@sha256:${'a'.repeat(64)}`
const rootImage = `mcr.microsoft.com/dotnet/runtime-deps@sha256:${'b'.repeat(64)}`
const manifest = readPrerequisiteManifest(path.join(repositoryRoot, 'eng', 'release-prerequisites.json'));
const frameworkSeeds = Object.freeze({
  clr2: `localhost:5000/framework-clr2@sha256:${'a'.repeat(64)}`,
  clr4: `localhost:5000/framework-clr4@sha256:${'b'.repeat(64)}`,
})

test('Framework seed build input closes exactly two shared companion identities', async () => {
  const spec = await createFrameworkSeedBuildSpec(repositoryRoot, wineImage, rootImage)

  assert.match(spec.inputSha256, /^[0-9a-f]{64}$/)
  assert.equal(spec.descriptor.strategy, 'framework-companion-seed-build-v1')
  assert.deepEqual(spec.images, frameworkSeedDefinitions)
  assert.deepEqual(spec.descriptor.files.map(file => file.path), [
    '.dockerignore',
    '.gitattributes',
    'deploy/docker/Dockerfile.operator-wine-framework-matrix',
    'deploy/docker/wine-netfx-framework-bootstrap.sh',
    'deploy/docker/wine-netfx-framework-preflight.sh',
    'deploy/docker/dedupe-wine-prefixes.py',
    'deploy/docker/certificates/microsoft-tls-rsa-root-g2-xsign.crt',
    'deploy/docker/certificates/microsoft-tls-g2-rsa-ca-ocsp-04.crt',
    'profiles/runtime-framework-installers.json',
    'eng/tools/prepare-framework-runtime.cs',
    'eng/release-prerequisites.json',
    'eng/prerequisites/dotnet-framework-2.0/NetFx64.exe',
  ])
  assert.deepEqual(
    spec.images.map(({ generation, version, prefix }) => ({ generation, version, prefix })),
    [
      { generation: 'clr2', version: '3.5', prefix: '/opt/wine-netfx-clr2' },
      { generation: 'clr4', version: '4.8', prefix: '/opt/wine-netfx-clr4' },
    ],
  )

  const changed = await createFrameworkSeedBuildSpec(
    repositoryRoot,
    `localhost:5000/wine@sha256:${'d'.repeat(64)}`,
    rootImage,
  )
  assert.notEqual(changed.inputSha256, spec.inputSha256)
})

test('operator image build input binds source recipes and both Framework seeds', async () => {
  const spec = await createOperatorImageBuildSpec(repositoryRoot, manifest, frameworkSeeds)

  assert.match(spec.inputSha256, /^[0-9a-f]{64}$/)
  assert.equal(spec.descriptor.strategy, 'source-built-operator-image-v1')
  assert.deepEqual(spec.images.map(({ id, buildKind }) => ({ id, buildKind })), [
    { id: 'jsharp20-development-base', buildKind: 'jsharp20' },
    { id: 'cppcli-prepared-base', buildKind: 'cppcli' },
  ])
  assert.deepEqual(spec.descriptor.files.map(file => file.path), [
    '.dockerignore',
    '.gitattributes',
    'deploy/docker/Dockerfile.operator-jsharp20',
    'deploy/docker/Dockerfile.operator-cppcli-base',
    'deploy/docker/cppcli-netfx-env.sh',
    'deploy/docker/extract-netfx48-sdk.py',
    'eng/tools/prepare-jsharp-toolchain.cs',
    'eng/tools/prepare-cppcli-toolchain.cs',
    'eng/release-prerequisites.json',
  ])

  const changed = await createOperatorImageBuildSpec(repositoryRoot, manifest, {
    ...frameworkSeeds,
    clr2: `localhost:5000/framework-clr2@sha256:${'c'.repeat(64)}`,
  })
  assert.notEqual(changed.inputSha256, spec.inputSha256)
})
