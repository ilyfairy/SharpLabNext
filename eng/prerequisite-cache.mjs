import crypto from 'node:crypto';
import fs from 'node:fs';
import https from 'node:https';
import path from 'node:path';
import { pipeline } from 'node:stream/promises';
import tls from 'node:tls';
import { fileURLToPath, pathToFileURL } from 'node:url';

const defaultRepositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const defaultManifestPath = path.join(defaultRepositoryRoot, 'eng', 'release-prerequisites.json');
const sourceIdentityModeEnvironmentVariable = 'SHARPLABNEXT_SOURCE_IDENTITY_MODE';
const contentSourceIdentityMode = 'content';
const sha256Pattern = /^[0-9a-f]{64}$/;
const imageIdPattern = /^sha256:[0-9a-f]{64}$/;
const maximumRedirects = 5;
const maximumAttempts = 4;
const approvedDownloadHosts = new Set(['pkgs.dev.azure.com', 'download.microsoft.com', 'download.visualstudio.microsoft.com', 'codeload.github.com'])
const httpsAgent = new https.Agent({
  ca: [...tls.getCACertificates('default'), ...tls.getCACertificates('system')],
})

export class PrerequisiteCacheError extends Error {
  constructor(message, options) {
    super(message, options)
    this.name = 'PrerequisiteCacheError'
  }
}

function fail(message, options) { throw new PrerequisiteCacheError(message, options); }

function isObject(value) { return value !== null && typeof value === 'object' && !Array.isArray(value); }

function exactKeys(value, keys, label) {
  if (!isObject(value) ||
      JSON.stringify(Object.keys(value).sort()) !== JSON.stringify([...keys].sort())) {
    fail(`${label} must contain exactly ${keys.join(', ')}`)
  }
}

function safeRelativePath(value, label) {
  if (typeof value !== 'string' || value.length === 0 || path.isAbsolute(value) ||
      value.includes('..') || value.includes('\\')) {
    fail(`${label} has an invalid relative path`)
  }
}

function validDownloadUrl(item) {
  if (item.kind === 'nuget-package') {
    const packageName = item.package.toLowerCase()
    const version = item.version.toLowerCase()
    let parsed
    try { parsed = new URL(item.url) } catch { return false }
    let segments
    try { segments = parsed.pathname.split('/').filter(Boolean).map(segment => decodeURIComponent(segment).toLowerCase()) } catch { return false }
    return parsed.protocol === 'https:' && approvedDownloadHosts.has(parsed.hostname) && segments.at(-3) === packageName && segments.at(-2) === version && segments.at(-1) === `${packageName}.${version}.nupkg`
  }
  if (item.kind !== 'file') return false
  let parsed
  try { parsed = new URL(item.url) } catch { return false }
  return parsed.protocol === 'https:' && approvedDownloadHosts.has(parsed.hostname)
}

export function readPrerequisiteManifest(filename = defaultManifestPath) {
  let bytes
  let value
  try {
    bytes = fs.readFileSync(filename)
    value = JSON.parse(bytes.toString('utf8'))
  } catch (error) {
    fail(`Could not read prerequisite manifest '${filename}': ${error.message}`, { cause: error })
  }
  exactKeys(
    value,
    ['schemaVersion', 'localRegistry', 'downloads', 'repositoryFiles', 'generatedImages'],
    'prerequisite manifest',
  )
  if (value.schemaVersion !== 3) fail('prerequisite manifest schemaVersion must be 3')
  exactKeys(
    value.localRegistry,
    ['image', 'imageId', 'containerName', 'host', 'port'],
    'localRegistry',
  )
  if (!/^registry@sha256:[0-9a-f]{64}$/.test(value.localRegistry.image) ||
      !imageIdPattern.test(value.localRegistry.imageId) ||
      value.localRegistry.host !== '127.0.0.1' || value.localRegistry.port !== 5000 ||
      value.localRegistry.containerName !== 'sharplabnext-release-registry') {
    fail('localRegistry must use the pinned loopback release registry contract')
  }
  if (!Array.isArray(value.downloads) || value.downloads.length === 0 ||
      !Array.isArray(value.repositoryFiles) ||
      !Array.isArray(value.generatedImages) || value.generatedImages.length === 0) {
    fail('prerequisite manifest must declare downloads, repositoryFiles, and generatedImages')
  }

  const ids = new Set()
  for (const item of value.downloads) {
    const isNuGetPackage = item?.kind === 'nuget-package'
    exactKeys(
      item,
      isNuGetPackage
        ? ['kind', 'id', 'path', 'url', 'package', 'version', 'sizeBytes', 'sha256', 'license']
        : ['kind', 'id', 'path', 'url', 'sizeBytes', 'sha256', 'license'],
      `download '${item?.id ?? '<unknown>'}'`,
    )
    if (!['file', 'nuget-package'].includes(item.kind)) {
      fail(`download '${item?.id ?? '<unknown>'}' has an invalid kind`)
    }
    if (typeof item.id !== 'string' ||
        !/^[a-z0-9][a-z0-9-]{0,63}$/.test(item.id) || ids.has(item.id)) {
      fail('download IDs must be unique safe identifiers')
    }
    ids.add(item.id)
    safeRelativePath(item.path, `download '${item.id}'`)
    if (isNuGetPackage) {
      if (typeof item.package !== 'string' ||
          !/^[A-Za-z0-9][A-Za-z0-9.]{0,127}$/.test(item.package) ||
          typeof item.version !== 'string' ||
          !/^[0-9A-Za-z][0-9A-Za-z.+-]{0,63}$/.test(item.version) ||
          path.basename(item.path).toLowerCase() !== `${item.package.toLowerCase()}.${item.version.toLowerCase()}.nupkg`) {
        fail(`download '${item.id}' has an invalid NuGet package identity`)
      }
    }
    if (typeof item.url !== 'string' || !validDownloadUrl(item)) {
      fail(`download '${item.id}' does not use an approved immutable HTTPS source`)
    }
    if (!Number.isSafeInteger(item.sizeBytes) || item.sizeBytes <= 0 ||
        !sha256Pattern.test(item.sha256)) {
      fail(`download '${item.id}' has an invalid size or SHA-256`)
    }
    if (typeof item.license !== 'string' || item.license.length === 0) {
      fail(`download '${item.id}' has no license description`)
    }
  }

  for (const item of value.repositoryFiles) {
    exactKeys(
      item,
      ['id', 'path', 'sizeBytes', 'sha256', 'gitLfs', 'license'],
      `repository file '${item?.id ?? '<unknown>'}'`,
    )
    if (typeof item.id !== 'string' ||
        !/^[a-z0-9][a-z0-9-]{0,63}$/.test(item.id) || ids.has(item.id)) {
      fail('repository file IDs must be unique safe identifiers')
    }
    ids.add(item.id)
    safeRelativePath(item.path, `repository file '${item.id}'`)
    if (!item.path.startsWith('eng/prerequisites/') || item.gitLfs !== true ||
        !Number.isSafeInteger(item.sizeBytes) || item.sizeBytes <= 0 ||
        !sha256Pattern.test(item.sha256)) {
      fail(`repository file '${item.id}' must be one exact Git LFS prerequisite`)
    }
    if (typeof item.license !== 'string' || item.license.length === 0) {
      fail(`repository file '${item.id}' has no license description`)
    }
  }

  for (const item of value.generatedImages) {
    exactKeys(
      item,
      ['id', 'reference', 'buildKind', 'license'],
      `generated image '${item?.id ?? '<unknown>'}'`,
    )
    if (typeof item.id !== 'string' ||
        !/^[a-z0-9][a-z0-9-]{0,63}$/.test(item.id) || ids.has(item.id)) {
      fail('generated image IDs must be unique safe identifiers')
    }
    ids.add(item.id)
    if (typeof item.reference !== 'string' || item.reference.length > 512 ||
        /\s|@/.test(item.reference) || !item.reference.includes(':')) {
      fail(`generated image '${item.id}' has an invalid tagged reference`)
    }
    if (typeof item.buildKind !== 'string' || !/^[a-z0-9][a-z0-9-]{0,63}$/.test(item.buildKind)) {
      fail(`generated image '${item.id}' has an invalid buildKind`)
    }
    if (typeof item.license !== 'string' || item.license.length === 0) {
      fail(`generated image '${item.id}' has no license description`)
    }
  }

  return Object.freeze({
    value,
    sha256: crypto.createHash('sha256').update(bytes).digest('hex'),
  })
}

async function fileSha256(filename) {
  const hash = crypto.createHash('sha256')
  await pipeline(fs.createReadStream(filename), hash)
  return hash.digest('hex')
}

async function verifyFile(filename, expectedSize, expectedSha256) {
  let info
  try { info = fs.lstatSync(filename) } catch { return false }
  if (!info.isFile() || info.isSymbolicLink() || info.size !== expectedSize) return false
  return await fileSha256(filename) === expectedSha256
}

function repositoryPath(repositoryRoot, relativePath, label) {
  const root = fs.realpathSync(repositoryRoot)
  const filename = path.resolve(root, ...relativePath.split('/'))
  if (!filename.startsWith(`${root}${path.sep}`)) fail(`${label} escapes the repository root`)
  return { root, filename }
}

function isLfsPointer(filename, info) {
  if (info.size < 1 || info.size > 1024) return false
  const bytes = fs.readFileSync(filename)
  return bytes.toString('utf8').startsWith('version https://git-lfs.github.com/spec/v1\n')
}

function requireLfsAttribute(root, relativePath, id) {
  let attributes
  try {
    attributes = fs.readFileSync(path.join(root, '.gitattributes'), 'utf8')
  } catch {
    fail(`Repository prerequisite '${id}' is not covered by a Git LFS filter rule`)
  }
  const normalizedPath = relativePath.replaceAll('\\', '/')
  const covered = attributes.split(/\r?\n/).some(line => {
    const content = line.replace(/#.*/, '').trim()
    if (content.length === 0) return false
    const fields = content.split(/\s+/)
    return fields[0] === normalizedPath && fields.slice(1).includes('filter=lfs')
  })
  if (!covered) {
    fail(`Repository prerequisite '${id}' is not covered by a Git LFS filter rule`)
  }
}

export async function validateRepositoryFiles(repositoryRoot, items) {
  const files = {}
  const contentIdentityMode = String(process.env[sourceIdentityModeEnvironmentVariable] ?? '').toLowerCase() === contentSourceIdentityMode;
  for (const item of items) {
    const { root, filename } = repositoryPath(repositoryRoot, item.path, `repository file '${item.id}'`);
    let info
    try { info = fs.lstatSync(filename) } catch {
      fail(
        `Repository Git LFS object is missing for '${item.id}' at '${item.path}'. ` +
        'Run git lfs pull before building.',
      )
    }
    if (!info.isFile() || info.isSymbolicLink()) {
      fail(`Repository prerequisite '${item.id}' must be one regular non-link file`)
    }
    if (isLfsPointer(filename, info)) {
      fail(
        `Repository prerequisite '${item.id}' is an unexpanded Git LFS pointer. ` +
        'Run git lfs pull before building.',
      )
    }
    if (!contentIdentityMode) requireLfsAttribute(root, item.path, item.id)
    if (!await verifyFile(filename, item.sizeBytes, item.sha256)) {
      fail(`Repository prerequisite '${item.id}' size or SHA-256 is invalid`)
    }
    files[item.id] = filename
  }
  return Object.freeze(files)
}

function ensureCacheDirectory(directory, label) {
  fs.mkdirSync(directory, { recursive: true })
  const info = fs.lstatSync(directory)
  if (!info.isDirectory() || info.isSymbolicLink()) {
    fail(`${label} must be a regular non-link directory`)
  }
  return fs.realpathSync(directory)
}

function requestDownload(url, headers, redirects = 0) {
  return new Promise((resolve, reject) => {
    const parsed = new URL(url)
    if (parsed.protocol !== 'https:') {
      reject(new Error('download redirects must remain on HTTPS'))
      return
    }
    const request = https.get(parsed, { headers, agent: httpsAgent }, response => {
      if ([301, 302, 303, 307, 308].includes(response.statusCode ?? 0)) {
        response.resume()
        if (redirects >= maximumRedirects || typeof response.headers.location !== 'string') {
          reject(new Error('download exceeded the redirect limit'))
          return
        }
        const target = new URL(response.headers.location, parsed).toString()
        if (new URL(target).protocol !== 'https:') {
          reject(new Error('download redirect attempted to leave HTTPS'))
          return
        }
        requestDownload(target, headers, redirects + 1).then(resolve, reject)
        return
      }
      resolve(response)
    })
    request.setTimeout(30_000, () => request.destroy(new Error('download request timed out')))
    request.on('error', reject)
  })
}

async function delay(milliseconds) {
  await new Promise(resolve => setTimeout(resolve, milliseconds))
}

async function downloadAsset(item, destination, offline, output) {
  if (await verifyFile(destination, item.sizeBytes, item.sha256)) {
    output.log(`Prerequisite cache hit: ${item.id}`)
    return
  }
  if (offline) fail(`Offline prerequisite '${item.id}' is missing or invalid at '${destination}'`)
  fs.mkdirSync(path.dirname(destination), { recursive: true })
  const part = `${destination}.part`
  if (fs.existsSync(destination)) fs.rmSync(destination)

  for (let attempt = 1; attempt <= maximumAttempts; attempt++) {
    try {
      let offset = 0
      try {
        const info = fs.lstatSync(part)
        if (!info.isFile() || info.isSymbolicLink() || info.size > item.sizeBytes) {
          fs.rmSync(part)
        } else {
          offset = info.size
        }
      } catch {}

      if (offset === item.sizeBytes) {
        if (await verifyFile(part, item.sizeBytes, item.sha256)) {
          fs.renameSync(part, destination)
          output.log(`Prerequisite verified: ${item.id}`)
          return
        }
        fs.rmSync(part)
        offset = 0
      }

      output.log(
        `Downloading ${item.id} (${offset}/${item.sizeBytes} bytes, ` +
        `attempt ${attempt}/${maximumAttempts})`,
      )
      const headers = {
        'Accept-Encoding': 'identity',
        'User-Agent': 'SharpLabNext-PrerequisiteCache/2',
      }
      if (offset > 0) headers.Range = `bytes=${offset}-`
      const response = await requestDownload(item.url, headers)
      const status = response.statusCode ?? 0
      if (status !== 200 && status !== 206) {
        response.resume()
        throw new Error(`HTTP ${status}`)
      }
      if (offset > 0 && status === 200) {
        response.destroy()
        fs.rmSync(part, { force: true })
        throw new Error('server ignored the resume range; restarting')
      }
      if (status === 206) {
        const range = response.headers['content-range']
        if (typeof range !== 'string' || !range.startsWith(`bytes ${offset}-`)) {
          response.destroy()
          throw new Error('server returned an invalid resume range')
        }
      }
      await pipeline(response, fs.createWriteStream(part, { flags: offset > 0 ? 'a' : 'w' }))
      const size = fs.statSync(part).size
      if (size > item.sizeBytes) {
        fs.rmSync(part)
        throw new Error('download exceeded the locked size')
      }
      if (size === item.sizeBytes && await verifyFile(part, item.sizeBytes, item.sha256)) {
        fs.renameSync(part, destination)
        output.log(`Prerequisite downloaded and verified: ${item.id}`)
        return
      }
      if (size === item.sizeBytes) {
        fs.rmSync(part)
        throw new Error('download SHA-256 does not match the lock')
      }
      throw new Error(`download stopped at ${size}/${item.sizeBytes} bytes`)
    } catch (error) {
      if (attempt === maximumAttempts) {
        fail(`Could not download '${item.id}': ${error.message}`, { cause: error })
      }
      await delay(500 * (2 ** (attempt - 1)))
    }
  }
}

function parseArguments(argv) {
  const result = {
    command: 'prepare',
    repositoryRoot: defaultRepositoryRoot,
    cacheRoot: undefined,
    offline: false,
    acceptMicrosoftLicenses: false,
  }
  let index = 0
  if (argv[0] !== undefined && !argv[0].startsWith('-')) result.command = argv[index++]
  for (; index < argv.length; index++) {
    const argument = argv[index]
    if (argument === '--offline') { result.offline = true; continue }
    if (argument === '--accept-microsoft-licenses') {
      result.acceptMicrosoftLicenses = true
      continue
    }
    if (argument === '--help' || argument === '-h') return { help: true }
    if (argument === '--repository-root' || argument === '--cache') {
      const value = argv[++index]
      if (value === undefined || value.length === 0) fail(`${argument} requires a value`)
      if (argument === '--repository-root') result.repositoryRoot = path.resolve(value)
      else result.cacheRoot = path.resolve(value)
      continue
    }
    fail(`Unknown prerequisite cache argument '${argument}'`)
  }
  if (!['prepare', 'fetch', 'verify'].includes(result.command)) {
    fail(`Unknown prerequisite cache command '${result.command}'`)
  }
  result.cacheRoot ??= path.join(result.repositoryRoot, 'artifacts', 'prerequisites')
  return result
}

function usage() {
  return 'Usage: node eng/prerequisite-cache.mjs [prepare|fetch|verify]\n' +
    '  [--repository-root PATH] [--cache PATH] [--offline] ' +
    '--accept-microsoft-licenses'
}

export async function runPrerequisiteCache(argv, output = console) {
  try {
    const options = parseArguments(argv)
    if (options.help) { output.log(usage()); return 0 }
    if (!options.acceptMicrosoftLicenses) {
      fail('--accept-microsoft-licenses is required to acquire or use Microsoft proprietary inputs')
    }
    const manifestPath = path.join(options.repositoryRoot, 'eng', 'release-prerequisites.json')
    const manifest = readPrerequisiteManifest(manifestPath)
    options.cacheRoot = ensureCacheDirectory(
      options.cacheRoot,
      'prerequisite cache root',
    )

    const downloads = {}
    for (const item of manifest.value.downloads) {
      const destination = path.resolve(options.cacheRoot, ...item.path.split('/'))
      if (!destination.startsWith(`${options.cacheRoot}${path.sep}`)) {
        fail(`download '${item.id}' escapes the cache root`)
      }
      const parent = ensureCacheDirectory(path.dirname(destination), `download '${item.id}' cache directory`);
      if (parent !== options.cacheRoot &&
          !parent.startsWith(`${options.cacheRoot}${path.sep}`)) {
        fail(`download '${item.id}' cache directory resolves outside the cache root`)
      }
      if (options.command === 'verify') {
        if (!await verifyFile(destination, item.sizeBytes, item.sha256)) {
          fail(`Prerequisite '${item.id}' is missing or invalid`)
        }
      } else {
        await downloadAsset(item, destination, options.offline, output)
      }
      downloads[item.id] = destination
    }

    const repositoryFiles = await validateRepositoryFiles(options.repositoryRoot, manifest.value.repositoryFiles);
    output.log(JSON.stringify({
      cacheRoot: options.cacheRoot,
      manifestSha256: manifest.sha256,
      downloads,
      repositoryFiles,
      generatedImages: Object.fromEntries(
        manifest.value.generatedImages.map(item => [item.id, item]),
      ),
      localRegistry: manifest.value.localRegistry,
    }))
    return 0
  } catch (error) {
    output.error(`Prerequisite cache error: ${error.message}`)
    return 1
  }
}

if (process.argv[1] !== undefined &&
    import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = await runPrerequisiteCache(process.argv.slice(2))
}
