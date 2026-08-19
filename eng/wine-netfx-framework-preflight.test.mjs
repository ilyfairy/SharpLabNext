import test from 'node:test'
import assert from 'node:assert/strict'
import childProcess from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const script = fs.readFileSync(
  path.join(repositoryRoot, 'deploy', 'docker', 'wine-netfx-framework-preflight.sh'),
  'utf8',
)
const dockerfile = fs.readFileSync(
  path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.runtime-wine-framework-matrix'),
  'utf8',
)
const shell = findShell()

function findShell() {
  if (process.platform !== 'win32') return '/bin/sh'

  const candidates = [
    process.env.ProgramFiles && path.join(process.env.ProgramFiles, 'Git', 'usr', 'bin', 'sh.exe'),
    process.env.ProgramFiles && path.join(process.env.ProgramFiles, 'Git', 'bin', 'sh.exe'),
  ].filter(Boolean)
  return candidates.find(candidate => fs.existsSync(candidate))
}

function shellPath(value) {
  return process.platform === 'win32' ? value.replaceAll('\\', '/') : value
}

function shellEnvironment() {
  if (process.platform !== 'win32' || shell === undefined) return process.env

  const gitUsrBin = path.dirname(shell)
  const gitRoot = path.resolve(gitUsrBin, '..', '..')
  return {
    ...process.env,
    PATH: [gitUsrBin, path.join(gitRoot, 'mingw64', 'bin'), process.env.PATH]
      .filter(Boolean)
      .join(path.delimiter),
  }
}

function wineSection(logicalPath) {
  return logicalPath.replaceAll('\\', '\\\\')
}

function runFixture({ requested, architecture = 'win64', sections, createSyswow64 = false }) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-netfx-preflight-'))
  const clr2 = path.join(root, 'wine-netfx-clr2')
  const clr4 = path.join(root, 'wine-netfx-clr4')
  const prefix = requested === '2.0' || requested === '3.0' || requested === '3.5' ? clr2 : clr4
  const framework = requested === '2.0' || requested === '3.0' || requested === '3.5'
    ? path.join(prefix, 'drive_c', 'windows', 'Microsoft.NET', 'Framework64', 'v2.0.50727')
    : path.join(prefix, 'drive_c', 'windows', 'Microsoft.NET', 'Framework64', 'v4.0.30319')

  try {
    fs.mkdirSync(framework, { recursive: true })
    fs.writeFileSync(path.join(framework, 'mscorlib.dll'), 'fixture')
    if (createSyswow64) {
      fs.mkdirSync(path.join(prefix, 'drive_c', 'windows', 'syswow64'), { recursive: true })
    }

    const registry = [
      'WINE REGISTRY Version 2',
      ';; All keys relative to \\\\Machine',
      '',
      `#arch=${architecture}`,
      '',
      ...sections.flatMap(section => [
        `[${wineSection(section.path)}] 1`,
        ...Object.entries(section.values).map(([name, value]) => `"${name}"=${value}`),
        '',
      ]),
    ].join('\r\n')
    fs.writeFileSync(path.join(prefix, 'system.reg'), registry)

    const fixtureScript = script
      .replaceAll('/opt/wine-netfx-clr2', shellPath(clr2))
      .replaceAll('/opt/wine-netfx-clr4', shellPath(clr4))
    const fixtureScriptPath = path.join(root, 'preflight.sh')
    fs.writeFileSync(fixtureScriptPath, fixtureScript)
    return childProcess.spawnSync(
      shell,
      [shellPath(fixtureScriptPath), shellPath(prefix), requested],
      { encoding: 'utf8', env: shellEnvironment() },
    )
  } finally {
    fs.rmSync(root, { recursive: true, force: true })
  }
}

test('Wine Framework preflight reads installer registry identity rather than directory names', () => {
  assert.match(script, /registry=\$\{prefix\}\/system\.reg/)
  assert.match(script, /#arch=/)
  assert.match(script, /architecture.*win64/s)
  assert.match(script, /REG_SECTION=.*awk/)
  assert.match(script, /Install/)
  assert.match(script, /v3\.0\\\\\\\\Setup/)
  assert.match(script, /InstallSuccess/)
  assert.match(script, /Version/)
  assert.match(script, /Release/)
  assert.doesNotMatch(script, /test ! -e .*syswow64/)
  assert.match(script, /Framework64/)
  assert.doesNotMatch(script, /wine-stable\s+.*\.exe/)
})

test('Wine Framework preflight accepts only Microsoft exact 4.5 through 4.8 Release values', () => {
  const official = [
    '0005c615',
    '0005c733', '0005c786',
    '0005cbf5',
    '0006004f', '00060051',
    '0006040e', '0006041f',
    '00060632', '00060636',
    '000707fe', '00070805',
    '000709fc', '000709fe',
    '00070bf0', '00070bf6',
    '00080ea8', '00080eb1', '00080ff4', '00081041',
  ]
  for (const release of official) assert.match(script, new RegExp(`\\b${release}\\b`))

  for (const unsupported of ['0005c737', '00060055', '00081043', '00081068', '0008107d']) {
    assert.doesNotMatch(script, new RegExp(`\\b${unsupported}\\b`))
  }
})

test('Wine Framework preflight accepts the canonical 3.0 installer key in a win64 prefix', {
  skip: shell === undefined,
}, () => {
  const result = runFixture({
    requested: '3.0',
    createSyswow64: true,
    sections: [{
      path: String.raw`Software\Microsoft\NET Framework Setup\NDP\v3.0\Setup`,
      values: { InstallSuccess: 'dword:00000001' },
    }],
  })

  assert.equal(result.status, 0, result.stderr)
  assert.match(result.stdout, /preflight passed: version=3\.0/)
})

test('Wine Framework preflight accepts the exact 2.0 RTM installer tuple without a Version value', {
  skip: shell === undefined,
}, () => {
  const result = runFixture({
    requested: '2.0',
    sections: [{
      path: String.raw`Software\Microsoft\NET Framework Setup\NDP\v2.0.50727`,
      values: {
        Increment: '"42"',
        Install: 'dword:00000001',
        MSI: 'dword:00000001',
        SP: 'dword:00000000',
      },
    }],
  })

  assert.equal(result.status, 0, result.stderr)
  assert.match(result.stdout, /preflight passed: version=2\.0/)
})

test('Wine Framework preflight rejects a different CLR 2 service level', {
  skip: shell === undefined,
}, () => {
  const result = runFixture({
    requested: '2.0',
    sections: [{
      path: String.raw`Software\Microsoft\NET Framework Setup\NDP\v2.0.50727`,
      values: {
        Increment: '"42"',
        Install: 'dword:00000001',
        MSI: 'dword:00000001',
        SP: 'dword:00000002',
      },
    }],
  })

  assert.equal(result.status, 1)
  assert.match(result.stderr, /does not identify \.NET Framework 2\.0 RTM SP=0/)
})

test('Wine Framework preflight rejects the old 3.0 parent Install heuristic', {
  skip: shell === undefined,
}, () => {
  const result = runFixture({
    requested: '3.0',
    sections: [{
      path: String.raw`Software\Microsoft\NET Framework Setup\NDP\v3.0`,
      values: {
        Install: 'dword:00000001',
        Version: '"3.0.30729.4926"',
      },
    }],
  })

  assert.equal(result.status, 1)
  assert.match(result.stderr, /v3\.0\\\\Setup.*InstallSuccess=1/)
})

test('Wine Framework preflight accepts an exact official Release and rejects a different row', {
  skip: shell === undefined,
}, () => {
  const sections = [{
    path: String.raw`Software\Microsoft\NET Framework Setup\NDP\v4\Full`,
    values: {
      Install: 'dword:00000001',
      Release: 'dword:00080eb1',
      Version: '"4.8.03761"',
    },
  }]

  const accepted = runFixture({ requested: '4.8', sections })
  assert.equal(accepted.status, 0, accepted.stderr)

  const rejected = runFixture({ requested: '4.7.2', sections })
  assert.equal(rejected.status, 1)
  assert.match(rejected.stderr, /does not identify \.NET Framework 4\.7\.2/)
})

test('Wine Framework preflight rejects a registry that declares a 32-bit prefix', {
  skip: shell === undefined,
}, () => {
  const result = runFixture({
    requested: '3.0',
    architecture: 'win32',
    sections: [{
      path: String.raw`Software\Microsoft\NET Framework Setup\NDP\v3.0\Setup`,
      values: { InstallSuccess: 'dword:00000001' },
    }],
  })

  assert.equal(result.status, 1)
  assert.match(result.stderr, /registry architecture 'win32' is not win64/)
})

test('Wine Framework preflight has an explicit exact-version branch for every candidate row', () => {
  for (const version of [
    '2.0',
    '3.0',
    '3.5',
    '4.0',
    '4.5',
    '4.5.1',
    '4.5.2',
    '4.6',
    '4.6.1',
    '4.6.2',
    '4.7',
    '4.7.1',
    '4.7.2',
    '4.8',
  ]) {
    assert.match(script, new RegExp('\\b' + version.replace('.', '\\.') + '\\b'))
  }
  assert.match(script, /unsupported exact \.NET Framework version/)
})

test('candidate Dockerfile validates the prefix before and after adding the target runtime helper', () => {
  assert.match(dockerfile, /COPY deploy\/docker\/wine-netfx-framework-preflight\.sh/)
  assert.match(dockerfile, /sharplabnext-wine-netfx-preflight \/opt\/wine-netfx-clr2/)
  assert.match(dockerfile, /sharplabnext-wine-netfx-preflight \/opt\/wine-netfx-clr4/)
  assert.match(dockerfile, /FROM wine-source AS runtime-base/)
  assert.match(dockerfile, /FROM runtime-base AS preflight/)
  assert.match(dockerfile, /FROM runtime-base AS final/)
  assert.match(dockerfile, /COPY --from=control-image \/usr\/share\/dotnet\/ \/usr\/share\/dotnet\//)
  assert.match(dockerfile, /sharplabnext-dedupe-wine-prefixes/)
  assert.match(dockerfile, /\.wine-prefix-layout\.json/)
  assert.match(dockerfile, /--verify/)
  assert.match(dockerfile, /hardlink-immutable-v1/)
  assert.match(dockerfile, /SharpLabNext\.TargetRuntimeRunner\.exe' self-test/)
  assert.match(dockerfile, /grep --count \./)
  assert.match(dockerfile, /&& \/usr\/local\/bin\/sharplabnext-wine-netfx-preflight/)
  assert.doesNotMatch(dockerfile, /COPY --from=wine-source \/usr\/ \/usr\//)
  assert.doesNotMatch(dockerfile, /(?:wine|control)-(?:os|libc)-id/)
  assert.doesNotMatch(dockerfile, /cmp --silent/)
  assert.doesNotMatch(dockerfile, /test ! -e .*syswow64/)
  assert.match(dockerfile, /test ! -e \/usr\/lib\/x86_64-linux-gnu\/wine\/i386-windows/)
})

test('other Wine matrix candidates use registry architecture instead of syswow64 presence', () => {
  for (const fileName of [
    'Dockerfile.runtime-wine-dotnet-matrix',
    'Dockerfile.runtime-mono-wine-matrix',
  ]) {
    const source = fs.readFileSync(path.join(repositoryRoot, 'deploy', 'docker', fileName), 'utf8')
    assert.doesNotMatch(source, /test ! -e .*syswow64/, `${fileName} must permit syswow64 in a win64 prefix`)
    assert.match(source, /system\.reg/, `${fileName} must inspect Wine registry metadata`)
    assert.ok(
      source.match(/index\(\$0, "#arch="\) == 1/g)?.length >= 2,
      `${fileName} must read the first architecture declaration before and after copying`,
    )
    assert.ok(
      source.match(/test ! -e \/usr\/lib\/x86_64-linux-gnu\/wine\/i386-windows/g)?.length >= 2,
      `${fileName} must reject the i386 Wine payload before and after copying`,
    )
  }
})

test('Wine CoreCLR prepares a bounded non-root XDG runtime directory through the shared entrypoint', () => {
  const entrypoint = fs.readFileSync(
    path.join(repositoryRoot, 'deploy', 'docker', 'runtime-entrypoint.sh'),
    'utf8',
  )
  const wineCoreClr = fs.readFileSync(
    path.join(repositoryRoot, 'deploy', 'docker', 'Dockerfile.runtime-wine-dotnet-matrix'),
    'utf8',
  )

  assert.match(wineCoreClr, /SHARPLABNEXT_PREPARE_WINE_XDG_RUNTIME_DIR=1/)
  assert.match(wineCoreClr, /SHARPLABNEXT_CAPTURE_DIRECTORY="Z:\\\\tmp"/)
  assert.match(wineCoreClr, /FROM runtime-base AS preflight\s+ARG DOTNET_RUNTIME_VERSION\s+USER 1654:1654/)
  assert.match(wineCoreClr, /output="\$\(\/opt\/sharplabnext\/runtime-entrypoint\.sh \/usr\/lib\/wine\/wine64/)
  assert.match(entrypoint, /SHARPLABNEXT_PREPARE_WINE_XDG_RUNTIME_DIR:-0/)
  assert.match(entrypoint, /\[ "\$\(id -u\)" != "0" \]/)
  assert.match(wineCoreClr, /ln -s \/tmp\/sharplabnext-wine-runtime-1654 \/run\/user\/1654/)
  assert.match(entrypoint, /xdg_storage_dir="\/tmp\/sharplabnext-wine-runtime-\$\{runtime_uid\}"/)
  assert.match(entrypoint, /xdg_runtime_dir="\/run\/user\/\$\{runtime_uid\}"/)
  assert.match(entrypoint, /\[ ! -L "\$\{xdg_runtime_dir\}" \]/)
  assert.match(entrypoint, /readlink "\$\{xdg_runtime_dir\}"/)
  assert.match(entrypoint, /stat -c %u "\$\{xdg_storage_dir\}"/)
  assert.match(entrypoint, /stat -c %a "\$\{xdg_storage_dir\}"/)
  assert.match(entrypoint, /XDG_RUNTIME_DIR="\$\{xdg_runtime_dir\}"\s+export XDG_RUNTIME_DIR/)
  assert.doesNotMatch(entrypoint, /XDG_RUNTIME_DIR="\$\{SHARPLABNEXT_/)
})
