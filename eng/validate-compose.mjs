import fs from 'node:fs'
import path from 'node:path'
import { spawnSync } from 'node:child_process'
import { fileURLToPath } from 'node:url'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const composeFiles = process.argv.slice(2)
const files = composeFiles.length > 0
  ? composeFiles
  : ['deploy/compose.dev.yaml', 'deploy/compose.prod.yaml']
const catalog = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, 'profiles/catalog/catalog.json'),
  'utf8',
))
const deploymentImages = JSON.parse(fs.readFileSync(
  path.join(repositoryRoot, 'deploy/images.json'),
  'utf8',
)).images

const failures = []

for (const relativeFile of files) {
  const normalizedRelativeFile = relativeFile.replaceAll('\\', '/')
  const isProduction = normalizedRelativeFile.endsWith('compose.prod.yaml')
  const result = spawnSync(
    'docker',
    ['compose', '-f', relativeFile, 'config', '--format', 'json'],
    { cwd: repositoryRoot, encoding: 'utf8', shell: false },
  )

  if (result.error) {
    failures.push(`${relativeFile}: docker compose could not start (${result.error.message})`)
    continue
  }
  if (result.status !== 0) {
    failures.push(`${relativeFile}: docker compose config failed (${result.stderr.trim()})`)
    continue
  }

  let config
  try {
    config = JSON.parse(result.stdout)
  } catch (error) {
    failures.push(`${relativeFile}: docker compose returned invalid JSON (${error.message})`)
    continue
  }

  validateConfig(config, relativeFile, isProduction)
}

if (failures.length > 0) {
  for (const failure of failures) console.error(`compose error: ${failure}`)
  process.exit(1)
}

console.log(`Validated ${files.length} Compose configurations and their security boundaries.`)

function validateConfig(config, fileName, isProduction) {
  const services = config.services ?? {}
  const serviceNames = Object.keys(services)
  const requiredServices = [
    'gateway',
    'artifact-store',
    'runtime-supervisor',
    'worker-roslyn-stable',
    'worker-roslyn-netfx48',
    'worker-artifacts-default',
  ]
  const toolchainWorkerServices = [
    'worker-roslyn-stable',
    'worker-roslyn-netfx48',
    'worker-roslyn-main',
    'worker-roslyn-const-generics',
    'worker-fsharp',
    'worker-gsharp',
    'worker-peachpie',
    'worker-cppcli',
    'worker-jsharp',
    'worker-il',
    'worker-minilang',
  ]

  for (const serviceName of requiredServices) {
    if (!(serviceName in services)) failures.push(`${fileName}: missing service ${serviceName}`)
  }
  for (const serviceName of toolchainWorkerServices) {
    if (!(serviceName in services)) {
      failures.push(`${fileName}: missing toolchain worker service ${serviceName}`)
    } else if (services[serviceName].environment?.ReferenceSetAttestation__Required !== 'true') {
      failures.push(`${fileName}: ${serviceName} must require reference-set attestations`)
    }
  }

  if (config.networks?.control?.internal !== true) {
    failures.push(`${fileName}: control network must be internal`)
  }
  if (config.networks?.['cppcli-control']?.internal !== true) {
    failures.push(`${fileName}: cppcli-control network must be internal`)
  }
  if (config.networks?.['jsharp-control']?.internal !== true) {
    failures.push(`${fileName}: jsharp-control network must be internal`)
  }

  if (!config.secrets?.['internal-service-token']?.file) {
    failures.push(`${fileName}: internal-service-token must come from a Compose secret file`)
  }
  if (isProduction && !config.secrets?.['github-oauth-client-secret']?.file) {
    failures.push(`${fileName}: github-oauth-client-secret must come from a Compose secret file`)
  }
  if (isProduction) validateDefaultGitHubOAuthSecretPlaceholder(config, fileName)
  validateLocalImageIdentityPolicy(services, fileName, isProduction)
  validateCompilerBurstCapacity(services, fileName)
  validateBoundedLocalLogging(services, fileName)
  if (isProduction) validateProductionGatewayBinding(services, fileName)

  for (const [serviceName, service] of Object.entries(services)) {
    if (service.network_mode === 'host') {
      failures.push(`${fileName}: ${serviceName} must not use host networking`)
    }

    const ports = service.ports ?? []
    if (serviceName !== 'gateway' && ports.length > 0) {
      failures.push(`${fileName}: only gateway may publish host ports (${serviceName} publishes ${ports.length})`)
    }

    const networks = networkNames(service.networks)
    if (serviceName === 'gateway') {
      if (!networks.has('public') || !networks.has('control')) {
        failures.push(`${fileName}: gateway must join public and control networks`)
      }
    } else if (networks.has('public')) {
      failures.push(`${fileName}: ${serviceName} must not join the public network`)
    }

    if (service.environment?.InternalServiceAuth__Required !== 'true') {
      failures.push(`${fileName}: ${serviceName} must require internal service authentication`)
    }
    if (service.environment?.InternalServiceAuth__TokenFile !== '/run/secrets/sharplabnext-internal-service-token') {
      failures.push(`${fileName}: ${serviceName} must read the shared token from the Compose secret mount`)
    }
    const internalServiceSecret = (service.secrets ?? []).some(secret =>
      secret.source === 'internal-service-token' && secret.target === 'sharplabnext-internal-service-token')
    if (!internalServiceSecret) {
      failures.push(`${fileName}: ${serviceName} must mount internal-service-token`)
    }

    for (const volume of service.volumes ?? []) {
      const source = typeof volume === 'string' ? volume.split(':', 1)[0] : volume.source
      const target = typeof volume === 'string' ? volume.split(':')[1] : volume.target
      const isDockerSocket = source === '/var/run/docker.sock' || target === '/var/run/docker.sock'
      if (isDockerSocket && serviceName !== 'runtime-supervisor') {
        failures.push(`${fileName}: only runtime-supervisor may mount the Docker socket (${serviceName})`)
      }
    }

    if (isProduction) {
      if (service.build !== undefined) failures.push(`${fileName}: production service ${serviceName} must not contain build`)
      if (service.pull_policy !== 'never') failures.push(`${fileName}: production service ${serviceName} must set pull_policy=never`)
      if (typeof service.image !== 'string' || service.image.endsWith(':latest')) {
        failures.push(`${fileName}: production service ${serviceName} must use a non-latest local image reference`)
      }

      const githubSecretMounted = (service.secrets ?? []).some(secret =>
        secret.source === 'github-oauth-client-secret' && secret.target === 'sharplabnext-github-oauth-client-secret')
      if (serviceName === 'gateway') {
        if (!githubSecretMounted) {
          failures.push(`${fileName}: gateway must mount github-oauth-client-secret`)
        }
        if (service.environment?.GitHub__OAuth__Enabled !== 'false') {
          failures.push(`${fileName}: production GitHub OAuth must default to explicitly disabled`)
        }
        if (service.environment?.GitHub__OAuth__ClientId !== '' ||
            service.environment?.GitHub__OAuth__CallbackUri !== '') {
          failures.push(`${fileName}: production GitHub OAuth identity must default to empty placeholders`)
        }
        if (service.environment?.GitHub__OAuth__ClientSecretFile !==
            '/run/secrets/sharplabnext-github-oauth-client-secret') {
          failures.push(`${fileName}: gateway must read the GitHub OAuth secret from its Compose secret mount`)
        }
        if (service.environment?.GitHub__OAuth__ClientSecret !== undefined) {
          failures.push(`${fileName}: production Gateway must not receive an inline GitHub OAuth client secret`)
        }
      } else if (githubSecretMounted) {
        failures.push(`${fileName}: only gateway may mount github-oauth-client-secret (${serviceName})`)
      }
    }
  }

  const supervisorVolumes = services['runtime-supervisor']?.volumes ?? []
  const supervisorHasSocket = supervisorVolumes.some(volume => {
    if (typeof volume === 'string') return volume.includes('/var/run/docker.sock')
    return volume.source === '/var/run/docker.sock' && volume.target === '/var/run/docker.sock'
  })
  if (!supervisorHasSocket) failures.push(`${fileName}: runtime-supervisor must reserve the Docker socket mount`)

  const forbiddenJobs = serviceNames.filter(name => /^(?:run|runner|jit|jit-inspector|runtime-job)(?:-|$)/i.test(name))
  if (forbiddenJobs.length > 0) {
    failures.push(`${fileName}: Run/JIT jobs must be one-shot sibling containers, not Compose services (${forbiddenJobs.join(', ')})`)
  }

  validateCppCliNetworkIsolation(services, fileName)
  validateJSharpNetworkIsolation(services, fileName)
}

function validateCppCliNetworkIsolation(services, fileName) {
  const gatewayNetworks = networkNames(services.gateway?.networks)
  if (!gatewayNetworks.has('cppcli-control')) {
    failures.push(`${fileName}: gateway must join cppcli-control for the isolated C++/CLI route`)
  }

  const cppCliNetworks = networkNames(services['worker-cppcli']?.networks)
  if (cppCliNetworks.size !== 1 || !cppCliNetworks.has('cppcli-control')) {
    failures.push(`${fileName}: worker-cppcli must join only cppcli-control`)
  }

  for (const [serviceName, service] of Object.entries(services)) {
    if (serviceName === 'gateway' || serviceName === 'worker-cppcli') continue
    if (networkNames(service.networks).has('cppcli-control')) {
      failures.push(`${fileName}: ${serviceName} must not join cppcli-control`)
    }
  }
}

function validateJSharpNetworkIsolation(services, fileName) {
  const gatewayNetworks = networkNames(services.gateway?.networks)
  if (!gatewayNetworks.has('jsharp-control')) {
    failures.push(`${fileName}: gateway must join jsharp-control for the isolated J# route`)
  }

  const jsharpNetworks = networkNames(services['worker-jsharp']?.networks)
  if (jsharpNetworks.size !== 1 || !jsharpNetworks.has('jsharp-control')) {
    failures.push(`${fileName}: worker-jsharp must join only jsharp-control`)
  }

  for (const [serviceName, service] of Object.entries(services)) {
    if (serviceName === 'gateway' || serviceName === 'worker-jsharp') continue
    if (networkNames(service.networks).has('jsharp-control')) {
      failures.push(`${fileName}: ${serviceName} must not join jsharp-control`)
    }
  }
}

function validateBoundedLocalLogging(services, fileName) {
  for (const [serviceName, service] of Object.entries(services)) {
    const logging = service.logging
    if (logging?.driver !== 'local') {
      failures.push(`${fileName}: ${serviceName} must use the local logging driver`)
      continue
    }

    if (logging.options?.['max-size'] !== '10m') {
      failures.push(`${fileName}: ${serviceName} local logs must set max-size=10m`)
    }
    if (logging.options?.['max-file'] !== '3') {
      failures.push(`${fileName}: ${serviceName} local logs must set max-file=3 as a YAML string`)
    }
  }
}

function validateProductionGatewayBinding(services, fileName) {
  const ports = services.gateway?.ports ?? []
  const gatewayPort = ports.find(port =>
    typeof port === 'object' && port.target === 8080 && port.protocol === 'tcp')
  if (!gatewayPort) {
    failures.push(`${fileName}: production gateway must publish TCP port 8080`)
    return
  }
  if (gatewayPort.host_ip !== '127.0.0.1') {
    failures.push(`${fileName}: production gateway must default to loopback host binding`)
  }
}

function validateCompilerBurstCapacity(services, fileName) {
  const gatewayConcurrency = services.gateway?.environment?.OperationExecution__WorkerConcurrency
  if (gatewayConcurrency !== '8') {
    failures.push(`${fileName}: gateway must explicitly dispatch eight operations for the release performance gate`)
  }

  const compilerWorkers = [
    ['worker-roslyn-stable', 'RoslynWorker__BuildProcess__MaximumConcurrentProcesses'],
    ['worker-roslyn-netfx48', 'RoslynWorker__BuildProcess__MaximumConcurrentProcesses'],
    ['worker-roslyn-main', 'RoslynWorker__BuildProcess__MaximumConcurrentProcesses'],
    ['worker-roslyn-const-generics', 'RoslynWorker__BuildProcess__MaximumConcurrentProcesses'],
    ['worker-fsharp', 'FSharpWorker__BuildProcess__MaximumConcurrentProcesses'],
    ['worker-peachpie', 'PeachPie__BuildProcess__MaximumConcurrentProcesses'],
  ]
  for (const [serviceName, setting] of compilerWorkers) {
    const value = services[serviceName]?.environment?.[setting]
    if (value !== gatewayConcurrency) {
      failures.push(`${fileName}: ${serviceName} compiler capacity must match Gateway operation concurrency (${gatewayConcurrency ?? 'missing'})`)
    }
  }

  const cppCli = services['worker-cppcli']
  if (cppCli?.user !== '0:0') {
    failures.push(`${fileName}: worker-cppcli must use the fixed root Wine/MSVC user`)
  }
  if (Number(cppCli?.pids_limit) !== 128) {
    failures.push(`${fileName}: worker-cppcli must reserve 128 PIDs for ASP.NET plus Wine/MSVC`)
  }
  if (Number(cppCli?.mem_limit) !== 1024 * 1024 * 1024) {
    failures.push(`${fileName}: worker-cppcli must have a 1 GiB container memory limit`)
  }
  if (Number(cppCli?.cpus) !== 1) {
    failures.push(`${fileName}: worker-cppcli must be limited to one CPU`)
  }
  const cppCliTmpfs = new Set((cppCli?.tmpfs ?? []).map(value =>
    typeof value === 'string' ? value : value.target))
  if (![...cppCliTmpfs].some(value => value.startsWith('/tmp:') && value.includes('exec'))) {
    failures.push(`${fileName}: worker-cppcli must have executable private /tmp tmpfs`)
  }
  if (![...cppCliTmpfs].some(value =>
      value.startsWith('/opt/wine-dotnet/drive_c/users/root/Temp:') && value.includes('exec'))) {
    failures.push(`${fileName}: worker-cppcli must have executable private Wine root Temp tmpfs`)
  }

  const jsharp = services['worker-jsharp']
  if (jsharp?.user !== '0:0') {
    failures.push(`${fileName}: worker-jsharp must use the fixed root Wine/J# user`)
  }
  if (Number(jsharp?.pids_limit) !== 128) {
    failures.push(`${fileName}: worker-jsharp must reserve 128 PIDs for ASP.NET plus Wine/J#`)
  }
  if (Number(jsharp?.mem_limit) !== 1024 * 1024 * 1024) {
    failures.push(`${fileName}: worker-jsharp must have a 1 GiB container memory limit`)
  }
  if (Number(jsharp?.cpus) !== 1) {
    failures.push(`${fileName}: worker-jsharp must be limited to one CPU`)
  }
  if (Number(jsharp?.ulimits?.nofile?.soft) !== 512 ||
      Number(jsharp?.ulimits?.nofile?.hard) !== 512) {
    failures.push(`${fileName}: worker-jsharp must limit nofile to 512:512`)
  }
  const jsharpTmpfs = new Set((jsharp?.tmpfs ?? []).map(value =>
    typeof value === 'string' ? value : value.target))
  if (![...jsharpTmpfs].some(value => value.startsWith('/tmp:') && value.includes('exec'))) {
    failures.push(`${fileName}: worker-jsharp must have executable private /tmp tmpfs`)
  }
  if (![...jsharpTmpfs].some(value =>
      value.startsWith('/opt/wine-jsharp20/drive_c/users/root/Temp:') && value.includes('exec'))) {
    failures.push(`${fileName}: worker-jsharp must have executable private Wine root Temp tmpfs`)
  }
}

function validateLocalImageIdentityPolicy(services, fileName, isProduction) {
  const gatewayEnvironment = services.gateway?.environment ?? {}
  const toolchains = new Map(catalog.toolchains.map(toolchain => [toolchain.id, toolchain]))
  const processors = new Map(catalog.artifactProcessors.map(processor => [processor.id, processor]))

  for (const image of deploymentImages) {
    if (!image.composeService || !image.imageIdEnvironment) continue

    const profile = image.toolchainId
      ? toolchains.get(image.toolchainId)
      : image.artifactProcessorId
        ? processors.get(image.artifactProcessorId)
        : undefined
    if (!profile?.workerId) continue

    const namespace = image.toolchainId ? 'LanguageWorkers' : 'ArtifactWorkers'
    const expectedKey = `Services__${namespace}__${profile.workerId}__ExpectedWorkerImageId`
    const expectedIdentity = gatewayEnvironment[expectedKey]
    const workerIdentity = services[image.composeService]?.environment?.[image.imageIdEnvironment]

    if (isProduction) {
      if (expectedIdentity !== 'bundle-overlay-required') {
        failures.push(`${fileName}: ${expectedKey} must default to bundle-overlay-required; the bundle overlay supplies the immutable ID`)
      }
      if (workerIdentity !== 'unverified') {
        failures.push(`${fileName}: ${image.composeService}.${image.imageIdEnvironment} must default to unverified; the bundle overlay supplies the immutable ID`)
      }
      continue
    }

    if (typeof expectedIdentity !== 'string' || expectedIdentity.startsWith('sha256:')) {
      failures.push(`${fileName}: ${expectedKey} must use the development image tag, not a generated image ID`)
    }
    if (workerIdentity !== expectedIdentity) {
      failures.push(`${fileName}: ${image.composeService}.${image.imageIdEnvironment} must match ${expectedKey}`)
    }
    if (services[image.composeService]?.image !== expectedIdentity) {
      failures.push(`${fileName}: ${image.composeService} must report the same development tag that Compose runs`)
    }
  }

  const supervisorEnvironment = services['runtime-supervisor']?.environment ?? {}
  for (const [key, runtimeIdentity] of Object.entries(supervisorEnvironment)) {
    const match = /^RuntimeSupervisor__Profiles__(\d+)__RuntimeImageId$/.exec(key)
    if (!match) continue

    if (isProduction) {
      if (runtimeIdentity !== 'unverified') {
        failures.push(`${fileName}: ${key} must default to unverified; the bundle overlay supplies the immutable ID`)
      }
      continue
    }

    const imageKey = `RuntimeSupervisor__Profiles__${match[1]}__Image`
    if (runtimeIdentity !== supervisorEnvironment[imageKey] ||
        typeof runtimeIdentity !== 'string' ||
        runtimeIdentity.startsWith('sha256:')) {
      failures.push(`${fileName}: ${key} must match ${imageKey} and use the development image tag`)
    }
  }
}

function validateDefaultGitHubOAuthSecretPlaceholder(config, fileName) {
  const configuredPath = config.secrets?.['github-oauth-client-secret']?.file
  if (typeof configuredPath !== 'string') return

  const composePath = path.resolve(repositoryRoot, fileName)
  const placeholderPath = path.resolve(
    path.dirname(composePath),
    'github-oauth-client-secret.disabled',
  )
  const normalizeForComparison = value => {
    const normalized = path.normalize(value)
    return process.platform === 'win32' ? normalized.toLowerCase() : normalized
  }
  if (normalizeForComparison(configuredPath) !== normalizeForComparison(placeholderPath)) return

  try {
    const stat = fs.statSync(placeholderPath)
    if (!stat.isFile()) {
      failures.push(`${fileName}: default GitHub OAuth secret placeholder must be a regular file`)
    } else if (stat.size !== 0) {
      failures.push(`${fileName}: default GitHub OAuth secret placeholder must be exactly 0 bytes`)
    }
  } catch (error) {
    failures.push(`${fileName}: default GitHub OAuth secret placeholder is unavailable (${error.message})`)
  }
}

function networkNames(networks) {
  if (Array.isArray(networks)) return new Set(networks)
  if (networks && typeof networks === 'object') return new Set(Object.keys(networks))
  return new Set()
}
