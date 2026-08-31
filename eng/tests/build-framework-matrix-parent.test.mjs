import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import {
  createParentBuildArguments,
  createParentDockerfile,
  inspectMetadataImage,
  runParentBuild,
  validateParentInputs,
} from '../build-framework-matrix-parent.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const installerManifestSha256 = crypto.createHash('sha256').update(fs.readFileSync(path.join(repositoryRoot, 'profiles', 'runtime-framework-installers.json'))).digest('hex');
const rowDefinitions = [
  ['netfx20', '2.0', 'clr2'],
  ['netfx30', '3.0', 'clr2'],
  ['netfx35', '3.5', 'clr2'],
  ['netfx40', '4.0', 'clr4'],
  ['netfx45', '4.5', 'clr4'],
  ['netfx451', '4.5.1', 'clr4'],
  ['netfx452', '4.5.2', 'clr4'],
  ['netfx46', '4.6', 'clr4'],
  ['netfx461', '4.6.1', 'clr4'],
  ['netfx462', '4.6.2', 'clr4'],
  ['netfx47', '4.7', 'clr4'],
  ['netfx471', '4.7.1', 'clr4'],
  ['netfx472', '4.7.2', 'clr4'],
  ['netfx48', '4.8', 'clr4'],
]

function makeContext() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-framework-parent-'))
  const rows = rowDefinitions.map(([id, version, clrGeneration], index) => ({
    id,
    version,
    clrGeneration,
    targetPrefix: clrGeneration,
    companionVersions: {
      clr2: clrGeneration === 'clr2' ? version : '3.5',
      clr4: clrGeneration === 'clr4' ? version : '4.8',
    },
    operatorImage: `registry.example/operator-${id}@sha256:${String(index + 1).padStart(64, '0')}`,
  }))
  for (const row of rows) {
    const rowRoot = path.join(root, 'rows', row.id)
    fs.mkdirSync(rowRoot, { recursive: true })
    fs.writeFileSync(path.join(rowRoot, 'row.json'), JSON.stringify({ schemaVersion: 1, ...row }) + '\n')
  }
  const input = {
    schemaVersion: 1,
    strategy: 'shared-framework-prefix-input-v1',
    rows,
  }
  const bytes = Buffer.from(JSON.stringify(input) + '\n')
  fs.writeFileSync(path.join(root, 'matrix-input.json'), bytes)
  return {
    root,
    input,
    digest: `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`,
  }
}

function values(context) {
  return {
    CONTEXT: context.root,
    ROOT_IMAGE: 'registry.example/root@sha256:' + 'a'.repeat(64),
    WINE_IMAGE: 'registry.example/wine@sha256:' + 'b'.repeat(64),
    FRAMEWORK_MATRIX_SOURCE_URI: 'docker://registry.example/framework-context@sha256:' + 'c'.repeat(64),
    FRAMEWORK_MATRIX_INPUT_SHA256: context.digest,
    SOURCE_REVISION: 'd'.repeat(40),
    IMAGE: 'sharplabnext/operator-framework-parent:test',
  }
}

function generatedDockerfile(context) { return path.join(context.root, 'Dockerfile.generated'); }

function parentLabels(input, revision) {
  return {
    'io.sharplabnext.framework.matrix': 'true',
    'io.sharplabnext.framework.matrix-strategy': 'shared-framework-target-prefix-matrix-v1',
    'io.sharplabnext.framework.dedupe-policy': 'wine-static-runtime-payload-v1',
    'org.opencontainers.image.revision': revision,
    'io.sharplabnext.source.revision': revision,
    'org.opencontainers.image.version': 'development',
    'io.sharplabnext.framework.matrix-input-sha256': input.FRAMEWORK_MATRIX_INPUT_SHA256,
    'io.sharplabnext.framework.matrix-source-uri': input.FRAMEWORK_MATRIX_SOURCE_URI,
    'io.sharplabnext.operator-image.wine': input.WINE_IMAGE,
    'io.sharplabnext.operator-root': input.ROOT_IMAGE,
  }
}

function operatorInspection(row, input) {
  return {
    Id: row.operatorImage.slice(row.operatorImage.lastIndexOf('@') + 1),
    Size: 1024,
    Os: 'linux',
    Architecture: 'amd64',
    Config: { Labels: {
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.framework.target-id': row.id,
      'io.sharplabnext.framework.version': row.version,
      'io.sharplabnext.framework.clr-generation': row.clrGeneration,
      'io.sharplabnext.wine-prefix-layout': 'hardlink-immutable-v1',
      'io.sharplabnext.wine-prefix-layout-manifest': '/opt/sharplabnext/.wine-prefix-layout.json',
      'io.sharplabnext.framework.installer-manifest-sha256': installerManifestSha256,
      'io.sharplabnext.operator-base': input.WINE_IMAGE.replace('/wine@', '/wine:development@'),
      'io.sharplabnext.operator-root': input.ROOT_IMAGE.replace('/root@', '/root:stable@'),
      'org.opencontainers.image.revision': input.SOURCE_REVISION === 'development'
        ? 'd'.repeat(40)
        : input.SOURCE_REVISION,
      'io.sharplabnext.source.revision': input.SOURCE_REVISION === 'development'
        ? 'd'.repeat(40)
        : input.SOURCE_REVISION,
    } },
  }
}

test('metadata image must resolve its digest and bind both source revision labels', () => {
  const reference = `registry.example/framework-context@sha256:${'c'.repeat(64)}`
  const matrixDigest = `sha256:${'d'.repeat(64)}`
  const revision = 'e'.repeat(40)
  const image = {
    Id: reference.slice(reference.lastIndexOf('@') + 1),
    RepoDigests: [reference],
    Size: 4096,
    Os: 'linux',
    Architecture: 'amd64',
    Config: { Labels: {
      'io.sharplabnext.framework.matrix-context': 'true',
      'io.sharplabnext.framework.matrix-content': 'metadata-only-v1',
      'io.sharplabnext.framework.matrix-strategy': 'shared-framework-prefix-input-v1',
      'io.sharplabnext.framework.matrix-input-sha256': matrixDigest,
      'io.sharplabnext.framework.matrix-row-count': '14',
      'org.opencontainers.image.revision': revision,
      'io.sharplabnext.source.revision': revision,
    } },
  }
  const inspect = value => () => ({ status: 0, stdout: JSON.stringify([value]) })

  assert.doesNotThrow(() => inspectMetadataImage(
    reference, matrixDigest, 14, revision, inspect(image),
  ))
  assert.throws(() => inspectMetadataImage(
    reference,
    matrixDigest,
    14,
    revision,
    inspect({ ...image, Id: `sha256:${'f'.repeat(64)}`, RepoDigests: [] }),
  ), /does not resolve to its supplied immutable digest/)
  assert.throws(() => inspectMetadataImage(
    reference,
    matrixDigest,
    14,
    revision,
    inspect({
      ...image,
      Config: { Labels: {
        ...image.Config.Labels,
        'io.sharplabnext.source.revision': 'f'.repeat(40),
      } },
    }),
  ), /io.sharplabnext.source.revision/)
})

test('shared parent accepts the exact metadata-only matrix and emits 14 target-prefix mounts', () => {
  const context = makeContext()
  try {
    assert.deepEqual(validateParentInputs(values(context)), [])
    const template = fs.readFileSync(path.join(repositoryRoot, 'deploy/docker/Dockerfile.operator-wine-framework-matrix-parent'), 'utf8')
    const generated = createParentDockerfile(template, context.input.rows)
    assert.equal((generated.match(/--mount=type=bind,from=framework-row-/g) ?? []).length, 14)
    for (const row of context.input.rows) {
      assert.match(
        generated,
        new RegExp(`source=/opt/wine-netfx-${row.targetPrefix},target=/run/sharplabnext-framework-rows/${row.id}/${row.targetPrefix},ro`),
      )
    }
    assert.doesNotMatch(generated, /SHARPLABNEXT_FRAMEWORK_ROW_MOUNTS/)
    assert.doesNotMatch(generated, /COPY --from=framework-matrix/)
    assert.match(generated, /FROM framework-row-netfx20 AS framework-tool-source/)
    assert.match(generated, /COPY --from=framework-tool-source \/usr\/ \/usr\//)
    assert.match(generated, /command -v python3 >\/dev\/null/)
    assert.doesNotMatch(generated, /FROM \$\{WINE_IMAGE\}/)
    assert.doesNotMatch(generated, /COPY --from=wine-source \/usr\/ \/usr\//)
    assert.match(generated, /--output \/opt\/sharplabnext/)

    const args = createParentBuildArguments(values(context), context.input, generatedDockerfile(context));
    assert.ok(args.includes(`framework-matrix-metadata=${context.root}`))
    for (const row of context.input.rows) {
      assert.ok(args.includes(`framework-row-${row.id}=docker-image://${row.operatorImage}`))
    }
    assert.ok(args.includes(`FRAMEWORK_MATRIX_INPUT_SHA256=${context.digest}`))
    assert.ok(args.includes('--load'))
    assert.equal(args.includes('--push'), false)
  } finally {
    fs.rmSync(context.root, { recursive: true, force: true })
  }
})

test('shared parent push mode uses immutable metadata and row contexts', () => {
  const context = makeContext()
  try {
    const input = {
      ...values(context),
      IMAGE: 'localhost:5000/sharplabnext/operator-framework-parent:release',
      push: true,
      metadataFile: 'C:\\Temp\\framework-parent-metadata.json',
    }
    assert.deepEqual(validateParentInputs(input), [])
    const args = createParentBuildArguments(input, context.input, generatedDockerfile(context));
    assert.ok(args.includes('--push'))
    assert.equal(args.includes('--load'), false)
    assert.deepEqual(
      args.slice(args.indexOf('--metadata-file'), args.indexOf('--metadata-file') + 2),
      ['--metadata-file', input.metadataFile],
    )
    assert.ok(args.includes(
      `framework-matrix-metadata=docker-image://${input.FRAMEWORK_MATRIX_SOURCE_URI.slice('docker://'.length)}`,
    ))
    const imageOnly = { ...input }
    delete imageOnly.CONTEXT
    assert.deepEqual(validateParentInputs(imageOnly), [])
    assert.ok(createParentBuildArguments(
      imageOnly, context.input, generatedDockerfile(context),
    ).includes(
      `framework-matrix-metadata=docker-image://${input.FRAMEWORK_MATRIX_SOURCE_URI.slice('docker://'.length)}`,
    ))

    const bare = { ...input, IMAGE: 'sharplabnext/operator-framework-parent:release' }
    assert.match(validateParentInputs(bare).join('\n'), /explicit registry host/)
    assert.match(validateParentInputs({
      ...input,
      ROOT_IMAGE: input.ROOT_IMAGE.replace('registry.example/', 'sharplabnext/'),
    }).join('\n'), /ROOT_IMAGE must include an explicit registry host/)
    assert.match(validateParentInputs({
      ...input,
      WINE_IMAGE: input.WINE_IMAGE.replace('registry.example/', 'sharplabnext/'),
    }).join('\n'), /WINE_IMAGE must include an explicit registry host/)
    assert.match(validateParentInputs({
      ...input,
      FRAMEWORK_MATRIX_SOURCE_URI: input.FRAMEWORK_MATRIX_SOURCE_URI.replace(
        'docker://registry.example/',
        'docker://sharplabnext/',
      ),
    }).join('\n'), /FRAMEWORK_MATRIX_SOURCE_URI must include an explicit registry host/)
    const localRows = {
      ...context.input,
      rows: context.input.rows.map((row, index) => index === 0
        ? { ...row, operatorImage: row.operatorImage.replace('registry.example/', 'sharplabnext/') }
        : row),
    }
    const imageOnlyWithLocalRow = { ...input }
    delete imageOnlyWithLocalRow.CONTEXT
    assert.match(
      validateParentInputs(imageOnlyWithLocalRow, localRows).join('\n'),
      /every Framework row operator image must include an explicit registry host/,
    )
    const development = { ...input, SOURCE_REVISION: 'development' }
    assert.match(validateParentInputs(development).join('\n'), /full Git commit/)
  } finally {
    fs.rmSync(context.root, { recursive: true, force: true })
  }
})

test('immutable metadata rejects a non-canonical row count before copying any row payload', () => {
  const context = makeContext()
  const malicious = {
    ...context.input,
    rows: [...context.input.rows, {
      ...context.input.rows.at(-1),
      id: 'netfx49',
      operatorImage: `registry.example/operator-netfx49@sha256:${'f'.repeat(64)}`,
    }],
  }
  const bytes = Buffer.from(JSON.stringify(malicious) + '\n')
  const digest = `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}`
  const source = `registry.example/framework-context@sha256:${'c'.repeat(64)}`
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-malicious-framework-context-'))
  const manifest = path.join(root, 'matrix-input.json')
  fs.writeFileSync(manifest, bytes)
  const copied = []
  const errors = []
  const spawn = (command, args) => {
    if (command === 'docker' && args[0] === 'image' && args[1] === 'inspect') {
      return { status: 0, stdout: JSON.stringify([{
        Id: source.slice(source.lastIndexOf('@') + 1),
        Size: 4096,
        Os: 'linux', Architecture: 'amd64',
        Config: { Labels: {
          'io.sharplabnext.framework.matrix-context': 'true',
          'io.sharplabnext.framework.matrix-content': 'metadata-only-v1',
          'io.sharplabnext.framework.matrix-strategy': 'shared-framework-prefix-input-v1',
          'io.sharplabnext.framework.matrix-input-sha256': digest,
          'io.sharplabnext.framework.matrix-row-count': '14',
          'org.opencontainers.image.revision': 'development',
          'io.sharplabnext.source.revision': 'development',
        } },
      }]) }
    }
    if (command === 'docker' && args[0] === 'create') {
      return { status: 0, stdout: `${'9'.repeat(64)}\n` }
    }
    if (command === 'docker' && args[0] === 'cp') {
      copied.push(args[1])
      assert.match(args[1], /:\/matrix-input\.json$/)
      fs.copyFileSync(manifest, args[2])
      return { status: 0, stdout: '' }
    }
    if (command === 'docker' && args[0] === 'rm') return { status: 0, stdout: '' }
    throw new Error(`unexpected command: ${command} ${args.join(' ')}`)
  }
  try {
    const status = runParentBuild([
      '--root-image', `registry.example/root@sha256:${'a'.repeat(64)}`,
      '--wine-image', `registry.example/wine@sha256:${'b'.repeat(64)}`,
      '--framework-matrix-source-uri', `docker://${source}`,
      '--framework-matrix-input-sha256', digest,
      '--source-revision', 'development',
      '--image', 'sharplabnext/operator-framework-parent:test',
    ], { SHARPLABNEXT_SOURCE_IDENTITY_MODE: 'content' }, spawn, { log() {}, error: value => errors.push(value) })
    assert.equal(status, 1)
    assert.equal(copied.length, 1)
    assert.match(errors.join('\n'), /exact 14-row Framework set/)
  } finally {
    fs.rmSync(context.root, { recursive: true, force: true })
    fs.rmSync(root, { recursive: true, force: true })
  }
})

test('operator provenance mismatch fails before BuildKit resolves the parent', () => {
  const context = makeContext()
  const input = values(context)
  const first = context.input.rows[0]
  const calls = []
  const errors = []
  const spawn = (command, args) => {
    calls.push([command, args])
    if (command === 'docker' && args[0] === 'image' && args[1] === 'inspect') {
      assert.equal(args[2], first.operatorImage)
      const inspection = operatorInspection(first, input)
      inspection.Config.Labels['io.sharplabnext.operator-base'] =
        `registry.example/other-wine@sha256:${'b'.repeat(64)}`
      return { status: 0, stdout: JSON.stringify([inspection]) }
    }
    throw new Error(`unexpected command: ${command} ${args.join(' ')}`)
  }
  try {
    const status = runParentBuild([
      '--context', context.root,
      '--root-image', input.ROOT_IMAGE,
      '--wine-image', input.WINE_IMAGE,
      '--framework-matrix-source-uri', input.FRAMEWORK_MATRIX_SOURCE_URI,
      '--framework-matrix-input-sha256', context.digest,
      '--source-revision', 'development',
      '--image', input.IMAGE,
    ], { SHARPLABNEXT_SOURCE_IDENTITY_MODE: 'content' }, spawn, { log() {}, error: value => errors.push(value) })
    assert.equal(status, 1)
    assert.match(errors.join('\n'), /netfx20 operator Wine\/base identity.*must equal/)
    assert.equal(calls.some(([, args]) => args[0] === 'buildx'), false)
  } finally {
    fs.rmSync(context.root, { recursive: true, force: true })
  }
})

test('shared parent push verifies the exact remote digest without pulling the large parent', () => {
  const context = makeContext()
  const revision = 'd'.repeat(40)
  const digest = `sha256:${'e'.repeat(64)}`
  const image = 'localhost:5000/sharplabnext/operator-framework-parent:release'
  const pinned = `localhost:5000/sharplabnext/operator-framework-parent@${digest}`
  const input = values(context)
  const calls = []
  const logs = []
  const errors = []
  const rowsByImage = new Map(context.input.rows.map(row => [row.operatorImage, row]))
  const spawn = (command, args) => {
    calls.push([command, args])
    if (command === 'docker' && args[0] === 'create') {
      assert.equal(args.at(-1), input.FRAMEWORK_MATRIX_SOURCE_URI.slice('docker://'.length))
      return { status: 0, stdout: `${'9'.repeat(64)}\n` }
    }
    if (command === 'docker' && args[0] === 'cp') {
      const source = args[1].slice(args[1].indexOf(':/') + 2)
      fs.copyFileSync(path.join(context.root, ...source.split('/')), args[2])
      return { status: 0, stdout: '' }
    }
    if (command === 'docker' && args[0] === 'rm') return { status: 0, stdout: '' }
    if (command === 'docker' && args[0] === 'image' && args[1] === 'inspect') {
      const reference = args[2]
      const row = rowsByImage.get(reference)
      if (row) return { status: 0, stdout: JSON.stringify([operatorInspection(row, input)]) }
      assert.equal(reference, input.FRAMEWORK_MATRIX_SOURCE_URI.slice('docker://'.length))
      return { status: 0, stdout: JSON.stringify([{
        Id: reference.slice(reference.lastIndexOf('@') + 1),
        Size: 4096,
        Os: 'linux', Architecture: 'amd64',
        Config: { Labels: {
          'io.sharplabnext.framework.matrix-context': 'true',
          'io.sharplabnext.framework.matrix-content': 'metadata-only-v1',
          'io.sharplabnext.framework.matrix-strategy': 'shared-framework-prefix-input-v1',
          'io.sharplabnext.framework.matrix-input-sha256': context.digest,
          'io.sharplabnext.framework.matrix-row-count': '14',
          'org.opencontainers.image.revision': revision,
          'io.sharplabnext.source.revision': revision,
        } },
      }]) }
    }
    if (command === 'git' && args[0] === 'rev-parse') return { status: 0, stdout: `${revision}\n` }
    if (command === 'git' && args[0] === 'status') return { status: 0, stdout: '' }
    if (command === 'git' && args[0] === 'show') {
      const relative = args[1].slice(args[1].indexOf(':') + 1)
      return { status: 0, stdout: fs.readFileSync(path.join(repositoryRoot, ...relative.split('/'))) }
    }
    if (command === 'docker' && args[0] === 'buildx' && args[1] === 'build') {
      const metadataIndex = args.indexOf('--metadata-file')
      assert.ok(metadataIndex > 0)
      const dockerfile = fs.readFileSync(args[args.indexOf('--file') + 1], 'utf8')
      assert.equal((dockerfile.match(/--mount=type=bind,from=framework-row-/g) ?? []).length, 14)
      fs.writeFileSync(args[metadataIndex + 1], JSON.stringify({ 'containerimage.digest': digest }))
      return { status: 0 }
    }
    if (command === 'docker' && args.slice(0, 3).join(' ') === 'buildx imagetools inspect') {
      assert.equal(args.at(-1), pinned)
      if (args.includes('--format')) {
        return {
          status: 0,
          stdout: JSON.stringify({
            manifest: { digest },
            image: {
              os: 'linux', architecture: 'amd64',
              config: { Labels: parentLabels(input, revision) },
            },
          }),
        }
      }
      assert.ok(args.includes('--raw'))
      return { status: 0, stdout: JSON.stringify({ layers: [{ size: 1024 }, { size: 2048 }] }) }
    }
    throw new Error(`unexpected command: ${command} ${args.join(' ')}`)
  }
  try {
    const status = runParentBuild([
      '--root-image', input.ROOT_IMAGE,
      '--wine-image', input.WINE_IMAGE,
      '--framework-matrix-source-uri', input.FRAMEWORK_MATRIX_SOURCE_URI,
      '--framework-matrix-input-sha256', context.digest,
      '--source-revision', revision,
      '--image', image,
      '--push',
    ], {}, spawn, { log: value => logs.push(value), error: value => errors.push(value) })
    assert.equal(status, 0, errors.join('\n'))
    assert.deepEqual(errors, [])
    assert.ok(calls.some(([, args]) => args.includes('--push') && !args.includes('--load')))
    assert.equal(calls.some(([, args]) => args[0] === 'pull' && args[1] === pinned), false)
    assert.match(logs.at(-1), new RegExp(`"registryReference": "${pinned}"`))
  } finally {
    fs.rmSync(context.root, { recursive: true, force: true })
  }
})

test('shared parent rejects repository-local contexts, raw row payloads, and manifest drift', () => {
  const context = makeContext()
  try {
    const local = { ...values(context), CONTEXT: repositoryRoot }
    assert.match(validateParentInputs(local).join('\n'), /outside the repository/)
    const rawPath = path.join(context.root, 'rows', 'netfx20', 'clr2')
    fs.mkdirSync(rawPath)
    assert.match(validateParentInputs(values(context)).join('\n'), /must contain only row\.json/)
    fs.rmSync(rawPath, { recursive: true })
    const drift = { ...values(context), FRAMEWORK_MATRIX_INPUT_SHA256: 'sha256:' + 'f'.repeat(64) }
    assert.match(validateParentInputs(drift).join('\n'), /does not match matrix-input\.json/)
  } finally {
    fs.rmSync(context.root, { recursive: true, force: true })
  }
})

test('content source revision requires content source identity', () => {
  const context = makeContext()
  try {
    const development = { ...values(context), SOURCE_REVISION: 'development' }
    assert.match(
      validateParentInputs(development).join('\n'),
      /content identity development/,
    )
    assert.deepEqual(validateParentInputs({ ...development, allowDirty: true }), [])
  } finally {
    fs.rmSync(context.root, { recursive: true, force: true })
  }
})

test('invalid parent inputs fail before Docker is invoked', () => {
  const calls = []
  const output = { log() {}, error() {} }
  const status = runParentBuild([
    '--context', 'C:\\not-real',
    '--root-image', 'registry.example/root:latest',
    '--wine-image', 'registry.example/wine:latest',
    '--framework-matrix-source-uri', 'https://example.invalid/context',
    '--framework-matrix-input-sha256', 'sha256:' + 'a'.repeat(64),
    '--source-revision', 'a'.repeat(40),
    '--image', 'sharplabnext/parent:test',
  ], {}, (...args) => { calls.push(args); return { status: 0, stdout: '' } }, output)
  assert.equal(status, 1)
  assert.equal(calls.length, 0)
})

test('a non-Docker source still requires an external metadata context', () => {
  const context = makeContext()
  try {
    const input = {
      ...values(context),
      FRAMEWORK_MATRIX_SOURCE_URI: 'https://example.invalid/framework-metadata.json',
    }
    delete input.CONTEXT
    assert.match(validateParentInputs(input).join('\n'), /CONTEXT is required/)
  } finally {
    fs.rmSync(context.root, { recursive: true, force: true })
  }
})
