import { spawn, spawnSync } from 'node:child_process';
import crypto from 'node:crypto';
import fs from 'node:fs';
import http from 'node:http';
import path from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';

import {
  createFrameworkSeedBuildSpec,
  createOperatorImageBuildSpec,
} from './image-build-inputs.mjs';
import {
  wineCoreClrOperatorExpectedLabels,
} from './build-runtime-candidate.mjs';
import {
  readPrerequisiteManifest,
  runPrerequisiteCache,
} from './prerequisite-cache.mjs';
import {
  wineCoreClrUserspaceEnvironment,
} from './runtime-wine-userspace-lock.mjs';

const defaultRepositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const sourceRevisionPattern = /^[0-9a-f]{40}(?:[0-9a-f]{24})?$/;
const sourceIdentityModeEnvironmentVariable = 'SHARPLABNEXT_SOURCE_IDENTITY_MODE';
const contentSourceIdentityMode = 'content';
const imageIdPattern = /^sha256:[0-9a-f]{64}$/;
const digestReferencePattern = /^[^@\s]+@sha256:[0-9a-f]{64}$/;
const capabilityIdPattern = /^[a-z0-9][a-z0-9._-]*$/;
const developmentInputsLabel = 'io.sharplabnext.development-image-inputs';
const sourceRevisionLabel = 'io.sharplabnext.source.revision';
const versionLabel = 'org.opencontainers.image.version';
const bakeEnvironmentJsonPrefix = 'SHARPLABNEXT_BAKE_ENVIRONMENT_JSON=';
const imageCacheProbePrefix = 'SHARPLABNEXT_IMAGE_CACHE=';
// A retry is for transient Docker/restore failures only.  BuildKit reuses
// completed layers, so one bounded retry is enough without hiding real errors.
const buildRetryAttempts = 2;
const buildRetryDelayMilliseconds = 3_000;
export function resolveOrdinaryBakeTarget(value, repositoryRoot = defaultRepositoryRoot) {
  const requested = String(value ?? '').trim();
  const document = readJson(path.join(repositoryRoot, 'deploy', 'images.json'), 'deployment image manifest');
  const definitions = Array.isArray(document?.images) ? document.images : [];
  const standalone = definitions.filter(definition => typeof definition?.ordinaryBakeTarget === 'string' && !(definition.buildCapabilities?.length > 0));
  const matches = standalone.filter(definition => definition.id === requested || definition.runtimeId === requested || definition.ordinaryBakeTarget === requested);
  if (matches.length !== 1) {
    const supported = standalone.map(definition => definition.ordinaryBakeTarget).sort().join(', ');
    fail(
      `Target '${requested}' is not a standalone ordinary image target. ` +
      `Supported targets: ${supported}. ` +
      'Use --all for the complete operator and runtime image graph.',
    );
  }
  const definition = matches[0];
  return Object.freeze({ bakeTarget: definition.ordinaryBakeTarget, imageName: definition.ordinaryBakeTarget, id: definition.id, runtimeId: definition.runtimeId, toolchainId: definition.toolchainId, artifactProcessorId: definition.artifactProcessorId, buildCapabilities: definition.buildCapabilities ?? [] });
}

function capabilityDefinitionsById(definitions) {
  if (!Array.isArray(definitions)) fail('Release image plan capabilityDefinitions must be an array');
  const result = new Map();
  for (const definition of definitions) {
    if (definition === null || typeof definition !== 'object' || Array.isArray(definition) ||
        typeof definition.id !== 'string' || !capabilityIdPattern.test(definition.id) || result.has(definition.id)) {
      fail('Release image plan contains an invalid or duplicate capability definition');
    }
    const dependencies = definition.dependencies ?? [];
    if (!Array.isArray(dependencies) || dependencies.some(dependency => typeof dependency !== 'string' || dependency.length === 0 || dependency === definition.id || dependencies.indexOf(dependency) !== dependencies.lastIndexOf(dependency))) {
      fail(`Capability '${definition.id}' has invalid or duplicate dependencies`);
    }
    if (definition.provisioner !== undefined && (definition.provisioner === null || typeof definition.provisioner !== 'object' || Array.isArray(definition.provisioner) || typeof definition.provisioner.kind !== 'string' || !capabilityIdPattern.test(definition.provisioner.kind) || typeof definition.provisioner.requiresPrerequisites !== 'undefined' && typeof definition.provisioner.requiresPrerequisites !== 'boolean' || typeof definition.provisioner.requiresRegistry !== 'undefined' && typeof definition.provisioner.requiresRegistry !== 'boolean' || definition.provisioner.seedGenerations !== undefined && (!Array.isArray(definition.provisioner.seedGenerations) || definition.provisioner.seedGenerations.some(value => typeof value !== 'string' || !capabilityIdPattern.test(value)) || new Set(definition.provisioner.seedGenerations).size !== definition.provisioner.seedGenerations.length))) {
      fail(`Capability '${definition.id}' has an invalid provisioner`);
    }
    const runtimeArguments = definition.runtimeArguments ?? [];
    if (!Array.isArray(runtimeArguments) || runtimeArguments.some(argument => argument === null || typeof argument !== 'object' || Array.isArray(argument) || typeof argument.option !== 'string' || !/^--[A-Za-z0-9][A-Za-z0-9-]*$/.test(argument.option) || typeof argument.sourceCapability !== 'string' || !capabilityIdPattern.test(argument.sourceCapability) || typeof argument.output !== 'string' || !/^[A-Za-z][A-Za-z0-9._-]*$/.test(argument.output)) || new Set(runtimeArguments.map(argument => argument.option)).size !== runtimeArguments.length) {
      fail(`Capability '${definition.id}' has invalid runtime arguments`);
    }
    result.set(definition.id, definition);
  }
  const reaches = (from, target, seen = new Set()) => {
    if (from === target) return true;
    if (!seen.add(from)) return false;
    return (result.get(from)?.dependencies ?? []).some(dependency => reaches(dependency, target, seen));
  };
  for (const definition of result.values()) {
    for (const dependency of definition.dependencies ?? []) {
      if (!result.has(dependency)) fail(`Capability '${definition.id}' depends on unknown capability '${dependency}'`);
    }
    for (const argument of definition.runtimeArguments ?? []) {
      if (!result.has(argument.sourceCapability)) fail(`Capability '${definition.id}' runtime argument references unknown capability '${argument.sourceCapability}'`);
      if (!reaches(definition.id, argument.sourceCapability)) fail(`Capability '${definition.id}' runtime argument source '${argument.sourceCapability}' must be itself or a dependency`);
    }
  }
  const visiting = new Set();
  const visited = new Set();
  const visit = id => {
    if (visited.has(id)) return;
    if (visiting.has(id)) fail(`Capability dependency cycle includes '${id}'`);
    visiting.add(id);
    for (const dependency of result.get(id).dependencies ?? []) visit(dependency);
    visiting.delete(id);
    visited.add(id);
  };
  for (const id of result.keys()) visit(id);
  return result;
}

function defaultCapabilityDefinitions() {
  try {
    return JSON.parse(fs.readFileSync(path.join(defaultRepositoryRoot, 'deploy', 'images.json'), 'utf8')).capabilityDefinitions ?? [];
  } catch {
    return [];
  }
}

function validPlanProducer(image) {
  const producer = image?.producer;
  if (!['bake', 'runtime-candidate', 'pull'].includes(producer?.kind) || typeof producer.id !== 'string' || producer.id.length === 0) return false;
  if (producer.kind === 'runtime-candidate') return typeof image.runtimeId === 'string' && producer.id === image.runtimeId;
  if (producer.kind === 'pull') return typeof image.runtimeId === 'string' && producer.id === image.reference;
  return capabilityIdPattern.test(producer.id);
}

export function resolveBuildCapabilities(images, definitions = defaultCapabilityDefinitions()) {
  const definitionsById = capabilityDefinitionsById(definitions);
  const capabilities = new Set();
  for (const image of images ?? []) {
    const declared = image?.buildCapabilities ?? [];
    if (!Array.isArray(declared)) fail('Image plan buildCapabilities must be an array');
    for (const capability of declared) {
      if (!definitionsById.has(capability)) fail(`Unknown build capability '${capability}' in image plan`);
      capabilities.add(capability);
    }
  }
  const pending = [...capabilities];
  while (pending.length > 0) {
    const capability = pending.pop();
    for (const dependency of definitionsById.get(capability).dependencies ?? []) {
      if (!capabilities.has(dependency)) {
        capabilities.add(dependency);
        pending.push(dependency);
      }
    }
  }
  return Object.freeze(capabilities);
}

function orderedCapabilityDefinitions(definitions, capabilities) {
  const definitionsById = capabilityDefinitionsById(definitions);
  const ordered = [];
  const visited = new Set();
  const visit = id => {
    if (visited.has(id)) return;
    visited.add(id);
    for (const dependency of definitionsById.get(id)?.dependencies ?? []) visit(dependency);
    ordered.push(definitionsById.get(id));
  };
  for (const id of capabilities) visit(id);
  return ordered;
}

function requiredCapabilityDefinitions(definitions, capabilities) {
  return orderedCapabilityDefinitions(definitions, capabilities).filter(definition => definition !== undefined);
}

function capabilityProvisioners(definitions, capabilities) {
  return requiredCapabilityDefinitions(definitions, capabilities).filter(definition => definition.provisioner !== undefined);
}

function imagesUsingProvisioner(images, definitions, provisionerKind) {
  const ids = new Set(definitions.filter(definition => definition.provisioner?.kind === provisionerKind).map(definition => definition.id));
  return images.filter(image => [...resolveBuildCapabilities([image], definitions)].some(capability => ids.has(capability)));
}

export function resolveRuntimeArguments(image, definitions, resources) {
  const definitionsById = capabilityDefinitionsById(definitions);
  const declared = new Set(image.buildCapabilities ?? []);
  for (const capability of declared) if (!definitionsById.has(capability)) fail(`Image '${image.id ?? image.producer?.id ?? '<unknown>'}' references unknown capability '${capability}'`);
  const ordered = orderedCapabilityDefinitions(definitions, resolveBuildCapabilities([image], definitions));
  const arguments_ = [];
  const optionValues = new Map();
  const isDependencyOf = (capability, dependency) => {
    const pending = [...(definitionsById.get(capability)?.dependencies ?? [])];
    const visited = new Set();
    while (pending.length > 0) {
      const current = pending.pop();
      if (current === dependency) return true;
      if (visited.has(current)) continue;
      visited.add(current);
      pending.push(...(definitionsById.get(current)?.dependencies ?? []));
    }
    return false;
  };
  for (const definition of ordered) {
    const capability = definition.id;
    for (const argument of definition.runtimeArguments ?? []) {
      const resource = resources?.[argument.sourceCapability];
      const value = resource?.[argument.output];
      if (typeof value !== 'string' || value.length === 0) fail(`Capability '${capability}' runtime argument '${argument.option}' requires output '${argument.output}' from '${argument.sourceCapability}'`);
      const previous = optionValues.get(argument.option);
      if (previous !== undefined) {
        if (!isDependencyOf(capability, previous.capability)) fail(`Image '${image.id ?? image.producer?.id ?? '<unknown>'}' receives duplicate runtime option '${argument.option}' from capability '${capability}'`);
        arguments_[previous.index + 1] = value;
        optionValues.set(argument.option, { value, capability, index: previous.index });
        continue;
      }
      optionValues.set(argument.option, { value, capability, index: arguments_.length });
      arguments_.push(argument.option, value);
    }
  }
  return arguments_;
}

function dependencyResource(definition, resources, output) {
  for (const dependency of definition.dependencies ?? []) {
    const value = resources?.[dependency]?.[output];
    if (typeof value === 'string' && value.length > 0) return value;
  }
  fail(`Capability '${definition.id}' requires dependency output '${output}'`);
}

export class BuildImagesError extends Error {
  constructor(message, options) {
    super(message, options);
    this.name = 'BuildImagesError';
  }
}

function fail(message, options) { throw new BuildImagesError(message, options); }

function run(command, arguments_, options = {}) {
  const result = spawnSync(command, arguments_, {
    cwd: options.cwd,
    env: options.env,
    encoding: options.capture ? 'utf8' : undefined,
    shell: false,
    stdio: options.capture ? ['ignore', 'pipe', 'pipe'] : 'inherit',
  });
  if (result.error !== undefined) fail(`Could not start '${command}': ${result.error.message}`, { cause: result.error });
  if (result.status !== 0) {
    const detail = options.capture ? String(result.stderr ?? '').trim() : '';
    fail(`'${command}' exited ${result.status ?? 1}${detail.length > 0 ? `: ${detail}` : ''}`);
  }
  return options.capture ? String(result.stdout ?? '') : '';
}

function start(command, arguments_, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, arguments_, {
      cwd: options.cwd,
      env: options.env,
      shell: false,
      stdio: 'inherit',
    });
    child.once('error', error => reject(new BuildImagesError(`Could not start '${command}': ${error.message}`, { cause: error })));
    child.once('exit', (code, signal) => {
      if (code === 0) resolve();
      else reject(new BuildImagesError(`'${command}' exited ${code ?? signal ?? 1}`));
    });
  });
}

function waitBeforeRetry(attempt) {
  const delay = buildRetryDelayMilliseconds * attempt;
  Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, delay);
}

function runWithRetry(command, arguments_, options = {}) {
  let lastError;
  for (let attempt = 1; attempt <= buildRetryAttempts; attempt++) {
    try {
      return run(command, arguments_, options);
    } catch (error) {
      lastError = error;
      if (attempt === buildRetryAttempts) throw error;
      console.warn(`Build command failed (attempt ${attempt}/${buildRetryAttempts}); retrying.`);
      waitBeforeRetry(attempt);
    }
  }
  throw lastError;
}

async function startWithRetry(command, arguments_, options = {}) {
  let lastError;
  for (let attempt = 1; attempt <= buildRetryAttempts; attempt++) {
    try {
      await start(command, arguments_, options);
      return;
    } catch (error) {
      lastError = error;
      if (attempt === buildRetryAttempts) throw error;
      console.warn(`Build command failed (attempt ${attempt}/${buildRetryAttempts}); retrying.`);
      await new Promise(resolve => setTimeout(resolve, buildRetryDelayMilliseconds * attempt));
    }
  }
  throw lastError;
}

export function validateLocalImageBuildDriverInspection(inspection) {
  const driver = /^Driver:\s*(\S+)\s*$/m.exec(inspection)?.[1];
  if (driver !== 'docker') {
    fail(
      `The local image build requires the Docker Buildx driver so it can ` +
      `consume source-built operator images from the host image store; ` +
      `observed '${driver ?? '<unknown>'}'. ` +
      'Select the Docker default builder and retry.',
    );
  }
}

function verifyLocalImageBuildDriver(repositoryRoot) {
  validateLocalImageBuildDriverInspection(run(
    'docker',
    ['buildx', 'inspect', '--bootstrap'],
    { cwd: repositoryRoot, capture: true },
  ));
}

export async function runParallel(tasks, maximumParallel) {
  let next = 0;
  let firstFailure;
  async function worker() {
    while (firstFailure === undefined) {
      const index = next++;
      if (index >= tasks.length) return;
      const task = tasks[index];
      try {
        await task.run();
      } catch (error) {
        const detail = error instanceof Error ? error.message : String(error);
        firstFailure ??= new BuildImagesError(`${task.label} failed: ${detail}`, { cause: error });
      }
    }
  }
  await Promise.all(Array.from({ length: Math.min(maximumParallel, tasks.length) }, worker));
  if (firstFailure !== undefined) throw firstFailure;
}

function readJson(filename, label) {
  try { return JSON.parse(fs.readFileSync(filename, 'utf8')); } catch (error) {
    fail(`Could not read ${label} '${filename}': ${error.message}`, { cause: error });
  }
}

function atomicWrite(filename, bytes) {
  fs.mkdirSync(path.dirname(filename), { recursive: true });
  const temporary = path.join(path.dirname(filename), `.${path.basename(filename)}.${process.pid}.${crypto.randomUUID()}.tmp`);
  try {
    fs.writeFileSync(temporary, bytes, { flag: 'wx' });
    fs.rmSync(filename, { force: true });
    fs.renameSync(temporary, filename);
  } finally {
    fs.rmSync(temporary, { force: true });
  }
}

export function applySourceVerificationMarker(options, output) {
  const verified = String(output).split(/\r?\n/).filter(line => line.startsWith('SHARPLABNEXT_SOURCE_VERIFIED=')).at(-1)?.slice('SHARPLABNEXT_SOURCE_VERIFIED='.length);
  if (verified !== undefined && verified !== 'true' && verified !== 'false') {
    fail('Source provenance resolver returned an invalid verification marker');
  }
  // Ordinary builds bind their labels to a content identity. Promotion and
  // signing tools independently require a verified clean Git source.
  return verified;
}

function resolveSourceRevision(options) {
  const arguments_ = [
    'run', path.join(options.repositoryRoot, 'eng', 'tools', 'resolve-source-provenance.cs'), '--',
    '--repository-root', options.repositoryRoot,
  ];
  if (options.sourceRevision !== undefined) arguments_.push('--source-revision', options.sourceRevision);
  const output = run('dotnet', arguments_, { cwd: options.repositoryRoot, capture: true });
  const revision = output.split(/\r?\n/).filter(line => line.startsWith('SHARPLABNEXT_SOURCE_REVISION=')).at(-1)?.slice('SHARPLABNEXT_SOURCE_REVISION='.length);
  if (!sourceRevisionPattern.test(revision ?? '')) fail('Source provenance resolver did not return a full revision');
  applySourceVerificationMarker(options, output);
  return revision;
}

export function validateReleaseImagePlan(plan) {
  if (plan?.schemaVersion !== 1 || typeof plan.releaseId !== 'string' ||
      !Array.isArray(plan.images) || plan.images.length === 0) fail('Release image plan is invalid');
  const capabilityDefinitions = plan.capabilityDefinitions ?? [];
  const definitionsById = capabilityDefinitionsById(capabilityDefinitions);
  const ids = new Set();
  const references = new Set();
  for (const image of plan.images) {
    if (typeof image?.id !== 'string' || ids.has(image.id) ||
        typeof image?.reference !== 'string' || references.has(image.reference) ||
        !validPlanProducer(image) ||
        (image.buildCapabilities !== undefined && !Array.isArray(image.buildCapabilities)) ||
        (image.buildCapabilities ?? []).some(capability => typeof capability !== 'string' || !definitionsById.has(capability)) ||
        new Set(image.buildCapabilities ?? []).size !== (image.buildCapabilities ?? []).length) fail('Release image plan contains an invalid or duplicate entry');
    ids.add(image.id);
    references.add(image.reference);
  }
  return capabilityDefinitions;
}

function generateImagePlan(options, sourceRevision) {
  const output = path.join(options.repositoryRoot, 'artifacts', 'release-image-plan.json');
  run('dotnet', [
    'run', '--project', path.join(options.repositoryRoot, 'src', 'Tools', 'SharpLabNext.BundleBuilder'),
    '--configuration', 'Release', '--',
    '--repository-root', options.repositoryRoot,
    '--write-image-plan', output,
    '--image-prefix', options.imagePrefix,
    '--source-revision', sourceRevision,
  ], { cwd: options.repositoryRoot });
  const plan = readJson(output, 'release image plan');
  const capabilityDefinitions = validateReleaseImagePlan(plan);
  const digest = `sha256:${crypto.createHash('sha256').update(JSON.stringify(plan)).digest('hex')}`;
  return { plan, path: output, digest, capabilityDefinitions };
}

function operatorEnvironmentInputs(operatorImages, capabilityDefinitions) {
  const inputs = {};
  for (const definition of capabilityDefinitions ?? []) {
    const operator = definition?.operator;
    if (operator?.environmentVariable === undefined) continue;
    const reference = operatorImages?.[operator.imageId];
    if (reference === undefined) continue;
    const name = String(operator.environmentVariable);
    if (!/^[A-Z][A-Z0-9_]*$/.test(name)) fail(`Capability '${definition.id}' has an invalid operator environment variable`);
    if (inputs[name] !== undefined && inputs[name] !== reference) fail(`Operator environment variable '${name}' is assigned more than once`);
    inputs[name] = reference;
  }
  return inputs;
}

function appendOperatorEnvironmentArguments(arguments_, operatorImages, capabilityDefinitions) {
  for (const [name, reference] of Object.entries(operatorEnvironmentInputs(operatorImages, capabilityDefinitions))) {
    arguments_.push('--development-image-input', `${name}=${reference}`);
  }
}

function bakeEnvironmentArguments(options, sourceRevision, operatorImages) {
  const arguments_ = [
    'run', path.join(options.repositoryRoot, 'eng', 'tools', 'run-with-bake-environment.cs'), '--',
    '--lock', path.join(options.repositoryRoot, 'profiles', 'lock.json'),
    '--base-images', path.join(options.repositoryRoot, 'profiles', 'base-images.json'),
    '--runtime-matrix', path.join(options.repositoryRoot, 'profiles', 'runtime-matrix.json'),
    '--source-revision', sourceRevision,
    '--repository-root', options.repositoryRoot,
    '--image-prefix', options.imagePrefix,
  ];
  appendOperatorEnvironmentArguments(arguments_, operatorImages, options.capabilityDefinitions);
  return arguments_;
}

function runInBakeEnvironment(
  options,
  sourceRevision,
  operatorImages,
  command,
  arguments_,
  snapshot = options.bakeEnvironmentSnapshot,
) {
  if (snapshot !== undefined) {
    return runWithRetry(command, arguments_, {
      cwd: options.repositoryRoot,
      env: createBakeChildEnvironment(snapshot, options, process.env, operatorImages),
    });
  }
  return runWithRetry('dotnet', [
    ...bakeEnvironmentArguments(options, sourceRevision, operatorImages),
    '--', command, ...arguments_,
  ], { cwd: options.repositoryRoot });
}

export function parseBakeEnvironmentSnapshot(output) {
  const payloads = String(output).split(/\r?\n/).filter(line => line.startsWith(bakeEnvironmentJsonPrefix)).map(line => line.slice(bakeEnvironmentJsonPrefix.length));
  if (payloads.length !== 1) {
    fail('Bake environment resolver did not emit exactly one JSON snapshot');
  }

  let document;
  try { document = JSON.parse(payloads[0]); } catch (error) {
    fail(`Bake environment resolver emitted invalid JSON: ${error.message}`, { cause: error });
  }
  if (document === null || typeof document !== 'object' || Array.isArray(document)) {
    fail('Bake environment resolver JSON must be an object');
  }

  const entries = Object.entries(document);
  if (entries.length === 0) fail('Bake environment resolver emitted an empty snapshot');
  for (const [name, value] of entries) {
    if (!/^[A-Z][A-Z0-9_]*$/.test(name) || typeof value !== 'string') {
      fail('Bake environment resolver JSON contains an invalid environment entry');
    }
  }
  return Object.freeze({ ...document });
}

function resolveBakeEnvironmentSnapshot(options, sourceRevision, operatorImages) {
  const output = runWithRetry('dotnet', [
    ...bakeEnvironmentArguments(options, sourceRevision, operatorImages),
    '--emit-environment-json',
  ], { cwd: options.repositoryRoot, capture: true });
  return parseBakeEnvironmentSnapshot(output);
}

export function createBakeChildEnvironment(snapshot, options, parentEnvironment = process.env, operatorImages = undefined) {
  const environment = { ...parentEnvironment, ...snapshot };
  if (options.sourceIdentityMode === contentSourceIdentityMode) {
    environment[sourceIdentityModeEnvironmentVariable] = contentSourceIdentityMode;
  }
  Object.assign(environment, operatorEnvironmentInputs(operatorImages, options.capabilityDefinitions));
  return environment;
}

function registryResponds() {
  return new Promise(resolve => {
    const request = http.get('http://127.0.0.1:5000/v2/', response => {
      response.resume();
      resolve(response.statusCode === 200);
    });
    request.setTimeout(2_000, () => { request.destroy(); resolve(false); });
    request.on('error', () => resolve(false));
  });
}

function inspectContainer(name, repositoryRoot) {
  const result = spawnSync('docker', ['container', 'inspect', name], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
    stdio: ['ignore', 'pipe', 'ignore'],
  });
  if (result.error !== undefined || result.status !== 0) return undefined;
  let document;
  try { document = JSON.parse(String(result.stdout ?? '')); } catch {
    fail(`Docker returned invalid container inspection JSON for '${name}'`);
  }
  if (!Array.isArray(document) || document.length !== 1) {
    fail(`Docker did not resolve exactly one container for '${name}'`);
  }
  return document[0];
}

export function validateRegistryContainer(container, configuration, requireManagedRestartPolicy = true) {
  if (container?.Image !== configuration.imageId ||
      container?.Config?.Image !== configuration.image ||
      (requireManagedRestartPolicy &&
       container?.HostConfig?.RestartPolicy?.Name !== 'unless-stopped')) {
    fail(
      `Container '${configuration.containerName}' does not match the pinned release ` +
      'registry image and restart policy',
    );
  }
  const bindings = container?.HostConfig?.PortBindings?.['5000/tcp'];
  if (!Array.isArray(bindings) || bindings.length !== 1 ||
      bindings[0]?.HostIp !== configuration.host ||
      bindings[0]?.HostPort !== String(configuration.port)) {
    fail(
      `Container '${configuration.containerName}' must bind only ` +
      `${configuration.host}:${configuration.port} to registry port 5000`,
    );
  }
}

function containersPublishingRegistryPort(configuration, repositoryRoot) {
  const result = spawnSync('docker', [
    'container', 'ls', '--all',
    '--filter', `publish=${configuration.port}`,
    '--format', '{{.ID}}',
  ], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
    stdio: ['ignore', 'pipe', 'ignore'],
  });
  if (result.error !== undefined || result.status !== 0) {
    fail('Could not inspect containers publishing the local registry port');
  }
  return String(result.stdout ?? '')
    .split(/\r?\n/)
    .map(value => value.trim())
    .filter(value => value.length > 0)
    .map(id => inspectContainer(id, repositoryRoot))
    .filter(container => container !== undefined)
    .filter(container => {
      const bindings = container?.HostConfig?.PortBindings?.['5000/tcp'];
      return Array.isArray(bindings) && bindings.some(binding =>
        binding?.HostIp === configuration.host &&
        binding?.HostPort === String(configuration.port));
    });
}

export async function ensureLocalRegistry(configuration, repositoryRoot) {
  let container = inspectContainer(configuration.containerName, repositoryRoot);
  let managed = container !== undefined;
  const responding = await registryResponds();
  if (container === undefined) {
    const compatible = containersPublishingRegistryPort(configuration, repositoryRoot);
    if (compatible.length > 1) {
      fail(`More than one container claims ${configuration.host}:${configuration.port}`);
    }
    if (compatible.length === 1) {
      container = compatible[0];
      validateRegistryContainer(container, configuration, false);
    } else if (responding) {
      fail(
        `${configuration.host}:${configuration.port} is occupied by a service that is not ` +
        'the pinned release registry container',
      );
    }
  }
  if (container !== undefined) validateRegistryContainer(container, configuration, managed);
  if (container?.State?.Running !== true && container !== undefined) {
    if (responding) fail('The managed release registry is stopped while its loopback port is occupied');
    run('docker', ['container', 'start', container.Id], { cwd: repositoryRoot });
  } else if (container === undefined) {
    run('docker', [
      'container', 'run', '--detach', '--restart', 'unless-stopped',
      '--name', configuration.containerName,
      '--publish', `${configuration.host}:${configuration.port}:5000`,
      configuration.image,
    ], { cwd: repositoryRoot });
    managed = true;
    container = inspectContainer(configuration.containerName, repositoryRoot);
  }
  if (container === undefined) fail('Docker did not retain the managed release registry container');
  container = inspectContainer(container.Id, repositoryRoot);
  if (container === undefined) fail('Docker lost the selected release registry container');
  validateRegistryContainer(container, configuration, managed);
  if (container.State?.Running !== true) fail('The managed release registry container is not running');
  for (let attempt = 0; attempt < 20; attempt++) {
    if (await registryResponds()) return;
    await new Promise(resolve => setTimeout(resolve, 250));
  }
  fail('Local release registry did not become ready on 127.0.0.1:5000');
}

function inspectImage(reference, repositoryRoot) {
  const output = run('docker', ['image', 'inspect', reference], { cwd: repositoryRoot, capture: true });
  let document;
  try { document = JSON.parse(output); } catch { fail(`Docker returned invalid inspection JSON for '${reference}'`); }
  if (!Array.isArray(document) || document.length !== 1) fail(`Docker did not resolve exactly one image for '${reference}'`);
  return document[0];
}

function imageRepository(reference) {
  const digest = reference.indexOf('@');
  if (digest > 0) return reference.slice(0, digest);
  const tag = reference.lastIndexOf(':');
  if (tag <= reference.lastIndexOf('/')) fail(`Image reference '${reference}' has no tag`);
  return reference.slice(0, tag);
}

function validateImageInspection(image, reference, expectedLabels, description, allowDifferentSourceRevision = false) {
  if (!imageIdPattern.test(image?.Id ?? '') || image?.Os !== 'linux' || image?.Architecture !== 'amd64') {
    fail(`${description} '${reference}' is not one immutable linux/amd64 image`);
  }
  const labels = image.Config?.Labels ?? {};
  const sourceExpected = expectedLabels[sourceRevisionLabel];
  const ociExpected = expectedLabels['org.opencontainers.image.revision'];
  const allowSourcePair = allowDifferentSourceRevision &&
    typeof sourceExpected === 'string' && sourceExpected === ociExpected;
  for (const [name, expected] of Object.entries(expectedLabels)) {
    if (allowDifferentSourceRevision && name === sourceRevisionLabel) {
      if (!sourceRevisionPattern.test(labels[name] ?? '')) fail(`${description} '${reference}' has an invalid source revision label`);
      continue;
    }
    if (allowSourcePair && name === 'org.opencontainers.image.revision') {
      if (!sourceRevisionPattern.test(labels[name] ?? '') || labels[name] !== labels[sourceRevisionLabel]) fail(`${description} '${reference}' has mismatched source revision labels`);
      continue;
    }
    if (labels[name] !== expected) {
      fail(
        `${description} '${reference}' label '${name}' is ` +
        `'${labels[name] ?? '<missing>'}', expected '${expected}'`,
      );
    }
  }
  return image;
}

export function validateReusableImageInspection(image, reference, expectedLabels, allowDifferentSourceRevision = true) {
  validateImageInspection(image, reference, expectedLabels, 'Cached prerequisite image', allowDifferentSourceRevision);
  const repository = imageRepository(reference);
  const digests = (image.RepoDigests ?? []).filter(value => value.startsWith(`${repository}@sha256:`));
  const digest = digests.find(value => digestReferencePattern.test(value));
  if (digest === undefined) {
    fail(`Cached prerequisite image '${reference}' has no unique immutable RepoDigest`);
  }
  return digest;
}

function tryInspectImage(reference, repositoryRoot) {
  const result = spawnSync('docker', ['image', 'inspect', reference], {
    cwd: repositoryRoot,
    encoding: 'utf8',
    shell: false,
  });
  if (result.error !== undefined) return undefined;
  if (result.status !== 0) return undefined;
  try { return inspectImage(reference, repositoryRoot); } catch { return undefined; }
}

// BuildKit owns layer reuse. This probe only looks in Docker's local image
// store; it never turns a cache miss into a registry pull or a second cache
// repository. Development images remain reusable across source revisions;
// explicit rebuild selectors are the opt-in invalidation mechanism.
function tryReuseLocalImage(reference, expectedLabels, repositoryRoot, enabled = true, allowDifferentSourceRevision = true) {
  if (!enabled) return undefined;
  const image = tryInspectImage(reference, repositoryRoot);
  if (image === undefined) return undefined;
  try {
    validateImageInspection(image, reference, expectedLabels, 'Local cached image', allowDifferentSourceRevision);
    console.log(`Build cache hit: ${reference} -> ${image.Id}`);
    return image;
  } catch {
    return undefined;
  }
}

function registryImageTag(options, name) {
  const prefix = String(options.imagePrefix).startsWith('localhost:5000/')
    ? String(options.imagePrefix)
    : `localhost:5000/${options.imagePrefix}`;
  return `${prefix}/${name}:${options.releaseId}`;
}

// Publish only the immutable identity required by a digest-pinned named
// context. This is transport, not a build cache: the source remains in the
// Docker image store and BuildKit still decides which layers are rebuilt.
function publishImmutableImage(source, destination, expectedLabels, repositoryRoot, allowDifferentSourceRevision = true) {
  const sourceImage = inspectImage(source, repositoryRoot);
  validateImageInspection(sourceImage, source, expectedLabels, 'Built image', allowDifferentSourceRevision);
  const existing = tryInspectImage(destination, repositoryRoot);
  if (existing?.Id === sourceImage.Id) {
    try { return validateReusableImageInspection(existing, destination, expectedLabels); } catch { /* republish */ }
  }
  pushAsLocalDigest(source, destination, repositoryRoot);
  const published = inspectImage(destination, repositoryRoot);
  return validateReusableImageInspection(published, destination, expectedLabels);
}

function pushAsLocalDigest(source, destination, repositoryRoot) {
  if (source !== destination) runWithRetry('docker', ['image', 'tag', source, destination], { cwd: repositoryRoot });
  runWithRetry('docker', ['image', 'push', destination], { cwd: repositoryRoot });
  const image = inspectImage(destination, repositoryRoot);
  const repository = destination.slice(0, destination.lastIndexOf(':'));
  const digest = (image.RepoDigests ?? []).find(value => value.startsWith(`${repository}@sha256:`));
  if (!digestReferencePattern.test(digest ?? '')) fail(`Pushed local image '${destination}' has no immutable RepoDigest`);
  return digest;
}

function buildWineOperator(options, sourceRevision, snapshot = undefined, requireImmutableReference = false) {
  const bakeSnapshot = snapshot ?? resolveBakeEnvironmentSnapshot(options, sourceRevision, undefined);
  const values = {
    ...bakeSnapshot,
    ...wineCoreClrUserspaceEnvironment(bakeSnapshot, options.repositoryRoot),
    SOURCE_REVISION: sourceRevision,
  };
  const sourceBinding = Object.freeze({ context: 'working-tree-content', promotionEligible: false });
  const expectedLabels = {
    ...wineCoreClrOperatorExpectedLabels(values, sourceBinding),
    'org.opencontainers.image.revision': sourceRevision,
    [sourceRevisionLabel]: sourceRevision,
    'io.sharplabnext.base-image.dotnet-runtime-deps': values.BASE_DOTNET_RUNTIME_DEPS_IMAGE,
    [developmentInputsLabel]: 'true',
  };
  // Keep one content tag across development and release identities. The
  // release-scoped tag is applied by the operator wrapper and remains only a
  // user-facing alias.
  const localTag = `${options.imagePrefix}/operator-wine-coreclr:content`;
  const releaseTag = `${options.imagePrefix}/operator-wine-coreclr:${options.releaseId}`;
  const cached = tryReuseLocalImage(
    localTag,
    expectedLabels,
    options.repositoryRoot,
    options.reuseExisting,
  );
  if (cached !== undefined) {
    const digest = requireImmutableReference
      ? publishImmutableImage(
        localTag,
        registryImageTag(options, 'operator-wine-coreclr'),
        expectedLabels,
        options.repositoryRoot,
      )
      : cached.Id;
    return { localTag, digest };
  }

  runInBakeEnvironment(options, sourceRevision, undefined, process.execPath, [path.join(options.repositoryRoot, 'eng', 'build-wine-coreclr-operator.mjs')], bakeSnapshot);
  const built = inspectImage(releaseTag, options.repositoryRoot);
  validateImageInspection(built, releaseTag, expectedLabels, 'Built image');
  runWithRetry('docker', ['image', 'tag', releaseTag, localTag], { cwd: options.repositoryRoot });
  const image = inspectImage(localTag, options.repositoryRoot);
  validateImageInspection(image, localTag, expectedLabels, 'Built image');
  const digest = requireImmutableReference
    ? publishImmutableImage(
      localTag,
      registryImageTag(options, 'operator-wine-coreclr'),
      expectedLabels,
      options.repositoryRoot,
    )
    : image.Id;
  return { localTag, digest };
}

function frameworkManifest(repositoryRoot) {
  const document = readJson(path.join(repositoryRoot, 'profiles', 'runtime-framework-installers.json'), 'Framework installer manifest');
  const matrix = readJson(path.join(repositoryRoot, 'profiles', 'runtime-matrix.json'), 'runtime matrix');
  const expectedIds = matrix?.framework?.targets?.map(target => target.id);
  const actualIds = document?.targets?.map(target => target.id);
  if (document?.schemaVersion !== 1 || !Array.isArray(actualIds) || actualIds.length === 0 || !Array.isArray(expectedIds) || JSON.stringify(actualIds) !== JSON.stringify(expectedIds) || new Set(actualIds).size !== actualIds.length) {
    fail('Framework installer manifest does not match the runtime matrix rows');
  }
  return document;
}

function baseImage(repositoryRoot, id) {
  const document = readJson(path.join(repositoryRoot, 'profiles', 'base-images.json'), 'base image manifest');
  const image = document?.images?.find(candidate => candidate.id === id);
  if (!digestReferencePattern.test(image?.reference ?? '')) fail(`Base image '${id}' is missing or not digest-pinned`);
  return image.reference;
}

async function buildFrameworkOperators(options, sourceRevision, wineDigest, downloads, targetIds = undefined, seedGenerations = undefined) {
  const manifest = frameworkManifest(options.repositoryRoot);
  downloads ??= {};
  targetIds ??= manifest.targets.map(target => target.id);
  const selectedTargetIds = new Set(targetIds);
  const selectedTargets = manifest.targets.filter(candidate => selectedTargetIds.has(candidate.id));
  const requiredSeedGenerations = new Set([
    ...(seedGenerations ?? []),
    ...selectedTargets.map(target => target.clrGeneration === 'clr2' ? 'clr4' : 'clr2'),
  ]);
  const rootImage = baseImage(options.repositoryRoot, 'dotnet-runtime-deps');
  if (requiredSeedGenerations.size === 0) {
    return {
      manifest,
      rootImage,
      references: new Map(),
      seedInputSha256: undefined,
      seedReferences: new Map(),
    };
  }
  const preparationScript = path.join(options.repositoryRoot, 'eng', 'tools', 'prepare-framework-runtime.cs');
  runWithRetry('dotnet', ['build', preparationScript, '--nologo'], { cwd: options.repositoryRoot });
  const seedSpec = await createFrameworkSeedBuildSpec(
    options.repositoryRoot,
    wineDigest,
    rootImage,
  );
  for (const generation of requiredSeedGenerations) if (!seedSpec.images.some(seed => seed.generation === generation)) fail(`Framework seed generation '${generation}' is not declared by the seed contract`);
  const commonTag = `${options.imagePrefix}/framework-wow64-base:content`;
  const commonRegistryTag = registryImageTag(options, 'framework-wow64-base');
  const commonArguments = [
    'run', preparationScript, '--no-build', '--',
    '--build-kind', 'wow64-base',
    '--repository-root', options.repositoryRoot,
    '--base-image', wineDigest,
    '--root-image', rootImage,
    '--output-image', commonTag,
    '--source-revision', sourceRevision,
    '--seed-input-sha256', seedSpec.inputSha256,
    '--accept-microsoft-dotnet-framework-eula',
  ];
  let commonImage = tryReuseLocalImage(
    commonTag,
    {
      'io.sharplabnext.framework.build-role': 'wow64-base',
      'io.sharplabnext.framework.seed-input-sha256': seedSpec.inputSha256,
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
    },
    options.repositoryRoot,
    options.reuseExisting,
  );
  if (commonImage === undefined) {
    runWithRetry('dotnet', commonArguments, { cwd: options.repositoryRoot });
    commonImage = inspectImage(commonTag, options.repositoryRoot);
    validateImageInspection(
      commonImage,
      commonTag,
      {
        'io.sharplabnext.framework.build-role': 'wow64-base',
        'io.sharplabnext.framework.seed-input-sha256': seedSpec.inputSha256,
        'io.sharplabnext.operator-only': 'true',
        'io.sharplabnext.redistribution': 'operator-supplied-only',
      },
      'Built image',
    );
  }
  const commonDigest = publishImmutableImage(
    commonTag,
    commonRegistryTag,
    {
      'io.sharplabnext.framework.build-role': 'wow64-base',
      'io.sharplabnext.framework.seed-input-sha256': seedSpec.inputSha256,
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
    },
    options.repositoryRoot,
  );

  const seedReferences = new Map();
  const missingSeeds = [];
  for (const seed of seedSpec.images.filter(candidate => requiredSeedGenerations.has(candidate.generation))) {
    const localTag = `${options.imagePrefix}/framework-companion-seed-${seed.generation}:content`;
    const registryTag = registryImageTag(options, `framework-companion-seed-${seed.generation}`);
    const expectedLabels = {
      'io.sharplabnext.framework.build-role': 'companion-seed',
      'io.sharplabnext.framework.seed-schema': 'framework-companion-seed-v1',
      'io.sharplabnext.framework.seed-generation': seed.generation,
      'io.sharplabnext.framework.seed-version': seed.version,
      'io.sharplabnext.framework.seed-prefix': seed.prefix,
      'io.sharplabnext.framework.seed-input-sha256': seedSpec.inputSha256,
      'io.sharplabnext.framework.installer-manifest-sha256': seedSpec.manifestSha256,
      'io.sharplabnext.framework.wow64-base-image': commonDigest,
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
    };
    const cached = tryReuseLocalImage(
      localTag,
      expectedLabels,
      options.repositoryRoot,
      options.reuseExisting,
    );
    if (cached !== undefined) {
      seedReferences.set(seed.generation, {
        ...seed,
        reference: localTag,
        digest: publishImmutableImage(
          localTag,
          registryTag,
          expectedLabels,
          options.repositoryRoot,
        ),
      });
      continue;
    }
    missingSeeds.push({ seed, localTag, registryTag, expectedLabels });
  }

  const seedTasks = missingSeeds.map(({ seed, localTag }) => ({
    label: `Framework companion seed '${seed.id}'`,
    run: async () => {
      const arguments_ = [
        'run', preparationScript, '--no-build', '--',
        '--build-kind', 'companion-seed',
        '--repository-root', options.repositoryRoot,
        '--seed-generation', seed.generation,
        '--framework-wow64-base-image', commonDigest,
        '--base-image', wineDigest,
        '--root-image', rootImage,
        '--output-image', localTag,
        '--source-revision', sourceRevision,
        '--seed-input-sha256', seedSpec.inputSha256,
        '--accept-microsoft-dotnet-framework-eula',
      ];
      const companion = manifest.companionPrefixes?.[seed.generation];
      const cachedPayload = manifest.cachedWinetricksPayloads?.find(payload => payload.verb === companion?.winetricksVerb);
      if (cachedPayload !== undefined) {
        const cachedFile = downloads?.[cachedPayload.prerequisiteId];
        if (typeof cachedFile !== 'string' || cachedFile.length === 0) fail(`Framework seed '${seed.id}' has no cached payload download`);
        arguments_.push(
          '--cached-winetricks-payload-file',
          cachedFile,
        );
      }
      await startWithRetry('dotnet', arguments_, { cwd: options.repositoryRoot });
    },
  }));
  await runParallel(seedTasks, options.maximumParallel);

  for (const { seed, localTag, registryTag, expectedLabels } of missingSeeds) {
    seedReferences.set(
      seed.generation,
      {
        ...seed,
        reference: localTag,
        digest: publishImmutableImage(
          localTag,
          registryTag,
          expectedLabels,
          options.repositoryRoot,
        ),
      },
    );
  }

  const references = new Map();
  const missingTargets = [];
  for (const target of selectedTargets) {
    const tag = `${options.imagePrefix}/operator-${target.id}:content`;
    const registryTag = registryImageTag(options, `operator-${target.id}`);
    const seed = seedReferences.get(target.clrGeneration === 'clr2' ? 'clr4' : 'clr2');
    if (seed === undefined) fail(`Framework target '${target.id}' has no companion seed`);
    const expectedLabels = {
      'org.opencontainers.image.title': 'SharpLabNext Operator Wine .NET Framework Matrix',
      'org.opencontainers.image.version': target.version,
      'org.opencontainers.image.revision': sourceRevision,
      [sourceRevisionLabel]: sourceRevision,
      'io.sharplabnext.runtime.framework': `.NETFramework,Version=v${target.version}`,
      'io.sharplabnext.runtime.framework-version': target.version,
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
      'io.sharplabnext.framework.target-id': target.id,
      'io.sharplabnext.framework.version': target.version,
      'io.sharplabnext.framework.clr-generation': target.clrGeneration,
      'io.sharplabnext.framework.companion-seed-image': seed.digest,
      'io.sharplabnext.framework.companion-seed-generation': seed.generation,
      'io.sharplabnext.framework.companion-seed-version': seed.version,
      'io.sharplabnext.framework.companion-seed-input-sha256': seedSpec.inputSha256,
      'io.sharplabnext.framework.installer-manifest-sha256': seedSpec.manifestSha256,
      'io.sharplabnext.wine-prefix-layout': 'hardlink-immutable-v1',
      'io.sharplabnext.wine-prefix-layout-manifest': '/opt/sharplabnext/.wine-prefix-layout.json',
      'io.sharplabnext.operator-base': wineDigest,
      'io.sharplabnext.operator-root': rootImage,
    };
    const cached = tryReuseLocalImage(
      tag,
      expectedLabels,
      options.repositoryRoot,
      options.reuseExisting,
    );
    if (cached !== undefined) {
      references.set(target.id, publishImmutableImage(
        tag,
        registryTag,
        expectedLabels,
        options.repositoryRoot,
      ));
    } else {
      missingTargets.push({ target, tag, registryTag, seed, expectedLabels });
    }
  }

  const tasks = missingTargets.map(({ target, tag, registryTag, seed, expectedLabels }) => ({
    label: `Framework operator '${target.id}'`,
    run: async () => {
      const arguments_ = [
        'run', preparationScript, '--no-build', '--',
        '--repository-root', options.repositoryRoot,
        '--target-id', target.id,
        '--base-image', wineDigest,
        '--root-image', rootImage,
        '--framework-seed-image', seed.digest,
        '--seed-input-sha256', seedSpec.inputSha256,
        '--output-image', tag,
        '--source-revision', sourceRevision,
        '--accept-microsoft-dotnet-framework-eula',
      ];
      if (target.recipe.kind === 'operator-installer') {
        const installer = Object.entries(downloads).find(([, filename]) => path.basename(filename).toLowerCase() === String(target.recipe.fileName).toLowerCase());
        if (installer === undefined) fail(`Framework target '${target.id}' has no locked installer download`);
        arguments_.push('--installer-secret-file', installer[1]);
      }
      const cachedPayload = manifest.cachedWinetricksPayloads?.find(payload => payload.verb === target.recipe.verb);
      if (cachedPayload !== undefined) {
        const cachedFile = downloads?.[cachedPayload.prerequisiteId];
        if (typeof cachedFile !== 'string' || cachedFile.length === 0) fail(`Framework target '${target.id}' has no cached payload download`);
        arguments_.push(
          '--cached-winetricks-payload-file',
          cachedFile,
        );
      }
      await startWithRetry('dotnet', arguments_, { cwd: options.repositoryRoot });
      references.set(target.id, publishImmutableImage(
        tag,
        registryTag,
        expectedLabels,
        options.repositoryRoot,
      ));
    },
  }));
  await runParallel(tasks, options.maximumParallel);
  return {
    manifest,
    rootImage,
    references,
    seedInputSha256: seedSpec.inputSha256,
    seedReferences,
  };
}

function confinedRepositoryFile(repositoryRoot, relativePath, label) {
  if (typeof relativePath !== 'string' || relativePath.length === 0 || path.isAbsolute(relativePath) || relativePath.includes('\\') || relativePath.split('/').some(segment => segment.length === 0 || segment === '.' || segment === '..')) {
    fail(`${label} has an invalid repository-relative path`);
  }
  const root = path.resolve(repositoryRoot);
  const filename = path.resolve(root, ...relativePath.split('/'));
  if (!filename.startsWith(`${root}${path.sep}`)) fail(`${label} escapes the repository root`);
  return filename;
}

function requiredOperatorDefinitions(capabilityDefinitions, capabilities) {
  return capabilityDefinitions.filter(definition => capabilities.has(definition.id) && definition.operator !== undefined);
}

function operatorDownloadArguments(operator, downloads) {
  const arguments_ = [];
  for (const item of operator.downloadArguments ?? []) {
    if (item === null || typeof item !== 'object' || typeof item.option !== 'string' || typeof item.downloadId !== 'string') fail('Operator download argument metadata is invalid');
    const value = downloads?.[item.downloadId];
    if (typeof value !== 'string' || value.length === 0) fail(`Operator download '${item.downloadId}' is missing from the prerequisite cache`);
    arguments_.push(item.option, value);
  }
  return arguments_;
}

async function buildOperatorImages(options, prerequisiteState, framework, manifest, capabilityDefinitions, requiredCapabilities) {
  if (manifest === undefined) fail('Operator image build requires a validated prerequisite manifest');
  const definitions = requiredOperatorDefinitions(capabilityDefinitions, requiredCapabilities);
  if (definitions.length === 0) return {};
  const frameworkSeeds = Object.fromEntries([...framework.seedReferences.entries()].map(([generation, seed]) => [generation, seed.digest]));
  const spec = await createOperatorImageBuildSpec(options.repositoryRoot, manifest, frameworkSeeds, definitions);
  const generatedImages = new Map(manifest.value.generatedImages.map(image => [image.id, image]));
  const result = {};
  const missingTasks = [];
  const imageMetadata = new Map();
  for (const definition of definitions) {
    const operator = definition.operator;
    const image = generatedImages.get(operator.imageId);
    if (image === undefined || (operator.buildKind !== undefined && image.buildKind !== operator.buildKind)) fail(`Capability '${definition.id}' references an invalid generated operator image`);
    const script = confinedRepositoryFile(options.repositoryRoot, operator.script, `Capability '${definition.id}' operator script`);
    const seed = operator.frameworkSeedGeneration === undefined ? undefined : frameworkSeeds[operator.frameworkSeedGeneration];
    if (operator.frameworkSeedGeneration !== undefined && typeof seed !== 'string') fail(`Capability '${definition.id}' references an unavailable framework seed '${operator.frameworkSeedGeneration}'`);
    const expectedLabels = {
      'io.sharplabnext.operator-build.strategy': 'source-built-operator-image-v1',
      'io.sharplabnext.operator-build.input-sha256': spec.inputSha256,
      'io.sharplabnext.operator-build.image-id': image.id,
      'io.sharplabnext.operator-build.build-kind': image.buildKind,
      ...(seed === undefined ? {} : { 'io.sharplabnext.operator-build.framework-seed-image': seed }),
      'io.sharplabnext.operator-only': 'true',
      'io.sharplabnext.redistribution': 'operator-supplied-only',
    };
    const registryTag = registryImageTag(options, image.id);
    imageMetadata.set(image.id, { registryTag, expectedLabels });
    const planned = { id: image.id, producer: { id: image.id }, buildCapabilities: [definition.id] };
    const cached = tryReuseLocalImage(image.reference, expectedLabels, options.repositoryRoot, options.reuseExisting && !isRebuildRequested(options, planned));
    if (cached !== undefined) {
      result[image.id] = publishImmutableImage(image.reference, registryTag, expectedLabels, options.repositoryRoot);
      continue;
    }
    const arguments_ = ['run', script, '--no-build', '--', '--repository-root', options.repositoryRoot];
    if (seed !== undefined) arguments_.push('--framework-seed-image', seed);
    arguments_.push('--output-image', image.reference, '--operator-build-input-sha256', spec.inputSha256, ...operatorDownloadArguments(operator, prerequisiteState.downloads), ...(operator.licenseArguments ?? []));
    missingTasks.push({
      id: image.id,
      script,
      label: `Source-built operator image '${definition.id}'`,
      run: () => startWithRetry('dotnet', arguments_, { cwd: options.repositoryRoot }),
    });
  }
  for (const script of [...new Set(missingTasks.map(task => task.script))]) runWithRetry('dotnet', ['build', script, '--nologo'], { cwd: options.repositoryRoot });
  await runParallel(missingTasks, options.maximumParallel);
  for (const task of missingTasks) {
    const metadata = imageMetadata.get(task.id);
    if (metadata === undefined) fail(`Operator image '${task.id}' has no cache metadata`);
    const image = generatedImages.get(task.id);
    result[task.id] = publishImmutableImage(image.reference, metadata.registryTag, metadata.expectedLabels, options.repositoryRoot);
  }
  return result;
}

function createFrameworkMatrixInput(options, built) {
  const rows = built.manifest.targets.map(target => ({
    id: target.id,
    version: target.version,
    clrGeneration: target.clrGeneration,
    targetPrefix: target.clrGeneration,
    companionVersions: target.clrGeneration === 'clr2'
      ? { clr2: target.version, clr4: '4.8' }
      : { clr2: '3.5', clr4: target.version },
    operatorImage: built.references.get(target.id),
  }));
  const value = { schemaVersion: 1, strategy: 'shared-framework-prefix-input-v1', rows };
  const bytes = `${JSON.stringify(value)}\n`;
  const filename = path.join(options.repositoryRoot, 'artifacts', 'prerequisites', 'generated', 'framework-matrix-input.json');
  atomicWrite(filename, bytes);
  return { filename, sha256: `sha256:${crypto.createHash('sha256').update(bytes).digest('hex')}` };
}

function buildFrameworkControlImages(options, sourceRevision, wineDigest, built, matrixInput) {
  const generatedRoot = path.join(options.repositoryRoot, 'artifacts', 'prerequisites', 'generated');
  const metadataTag = `${options.imagePrefix}/operator-framework-metadata:content`;
  const metadataRegistryTag = registryImageTag(options, 'operator-framework-metadata');
  const metadataLabels = {
    'io.sharplabnext.framework.matrix-context': 'true',
    'io.sharplabnext.framework.matrix-content': 'metadata-only-v1',
    'io.sharplabnext.framework.matrix-strategy': 'shared-framework-prefix-input-v1',
    'io.sharplabnext.framework.matrix-input-sha256': matrixInput.sha256,
    'io.sharplabnext.framework.matrix-row-count': String(built.manifest.targets.length),
    'org.opencontainers.image.revision': sourceRevision,
    'io.sharplabnext.source.revision': sourceRevision,
  };
  let metadataImage = tryReuseLocalImage(
    metadataTag,
    metadataLabels,
    options.repositoryRoot,
    options.reuseExisting,
  );
  if (metadataImage === undefined) {
    const contextArguments = [
      path.join(options.repositoryRoot, 'eng', 'build-framework-matrix-context.mjs'),
      '--matrix-input', matrixInput.filename,
      '--source-revision', sourceRevision,
      '--image', metadataTag,
      '--version', options.releaseId,
    ];
    runWithRetry(process.execPath, contextArguments, { cwd: options.repositoryRoot });
    metadataImage = inspectImage(metadataTag, options.repositoryRoot);
    validateImageInspection(metadataImage, metadataTag, metadataLabels, 'Built image');
  }
  const metadataDigest = publishImmutableImage(
    metadataTag,
    metadataRegistryTag,
    metadataLabels,
    options.repositoryRoot,
  );

  const parentTag = `${options.imagePrefix}/operator-framework-parent:content`;
  const parentRegistryTag = registryImageTag(options, 'operator-framework-parent');
  const parentLabels = {
    'io.sharplabnext.framework.matrix': 'true',
    'io.sharplabnext.framework.matrix-strategy': 'shared-framework-target-prefix-matrix-v1',
    'io.sharplabnext.framework.dedupe-policy': 'wine-static-runtime-payload-v1',
    'org.opencontainers.image.revision': sourceRevision,
    'io.sharplabnext.source.revision': sourceRevision,
    'io.sharplabnext.framework.matrix-input-sha256': matrixInput.sha256,
    'io.sharplabnext.framework.matrix-source-uri': `docker://${metadataDigest}`,
    'io.sharplabnext.operator-image.wine': wineDigest,
    'io.sharplabnext.operator-root': built.rootImage,
  };
  let parentImage = tryReuseLocalImage(
    parentTag,
    parentLabels,
    options.repositoryRoot,
    options.reuseExisting,
  );
  if (parentImage === undefined) {
    const parentArguments = [
      path.join(options.repositoryRoot, 'eng', 'build-framework-matrix-parent.mjs'),
      '--root-image', built.rootImage,
      '--wine-image', wineDigest,
      '--framework-matrix-source-uri', `docker://${metadataDigest}`,
      '--framework-matrix-input-sha256', matrixInput.sha256,
      '--source-revision', sourceRevision,
      '--image', parentTag,
      '--version', options.releaseId,
    ];
    runWithRetry(process.execPath, parentArguments, { cwd: options.repositoryRoot });
    parentImage = inspectImage(parentTag, options.repositoryRoot);
    validateImageInspection(parentImage, parentTag, parentLabels, 'Built image');
  }
  const parentDigest = publishImmutableImage(
    parentTag,
    parentRegistryTag,
    parentLabels,
    options.repositoryRoot,
  );

  const candidateInput = path.join(generatedRoot, 'runtime-framework-candidates.json');
  fs.rmSync(candidateInput, { force: true });
  run(process.execPath, [
    path.join(options.repositoryRoot, 'eng', 'create-runtime-framework-candidate-input.mjs'),
    '--parent-image', parentDigest,
    '--metadata-image', metadataDigest,
    '--matrix-input', matrixInput.filename,
    '--source-revision', sourceRevision,
    '--output', candidateInput,
  ], { cwd: options.repositoryRoot });
  return { candidateInput, metadataDigest, parentDigest };
}

function buildBakeTargets(options, sourceRevision, operatorImages, targets, environmentSnapshot = undefined) {
  if (targets.length === 0) return;
  runInBakeEnvironment(options, sourceRevision, operatorImages, 'docker', [
    'buildx', 'bake', '--file', path.join(options.repositoryRoot, 'eng', 'bake.hcl'), ...targets,
  ], environmentSnapshot);
}

function ordinaryImageExpectedLabels(sourceRevision, releaseId) {
  return {
    [versionLabel]: releaseId,
    [sourceRevisionLabel]: sourceRevision,
    [developmentInputsLabel]: 'true',
  };
}

async function runOrdinaryImageBuild(options, sourceRevision, output) {
  const target = resolveOrdinaryBakeTarget(options.target, options.repositoryRoot);
  const ordinaryPlan = { id: target.id, runtimeId: target.runtimeId, toolchainId: target.toolchainId, artifactProcessorId: target.artifactProcessorId, buildCapabilities: target.buildCapabilities, producer: { id: target.bakeTarget } };
  if (options.rebuildTargets?.some(selector => !matchesRebuildTarget(ordinaryPlan, selector))) {
    fail(`--rebuild-target does not match ordinary target '${target.bakeTarget}'; use --all to select release images`);
  }
  // Resolve the same lock/base-image environment used by Bake, but do not
  // create a release plan or start any prerequisite/operator orchestration.
  const environmentSnapshot = resolveBakeEnvironmentSnapshot(options, sourceRevision, undefined);
  const releaseId = environmentSnapshot.RELEASE_ID;
  if (typeof releaseId !== 'string' || releaseId.length === 0) {
    fail('Bake environment did not provide a release id for the ordinary image');
  }
  options.releaseId = releaseId;
  const reference = `${options.imagePrefix}/${target.imageName}:${releaseId}`;
  const expectedLabels = ordinaryImageExpectedLabels(sourceRevision, releaseId);
  output.log(`Ordinary image target: ${target.bakeTarget} -> ${reference}`);
  output.log(`Source identity: ${sourceRevision}`);

  if (options.planOnly) return 0;

  const cached = tryReuseLocalImage(
    reference,
    expectedLabels,
    options.repositoryRoot,
    options.reuseExisting && !isRebuildRequested(options, ordinaryPlan),
  );
  if (options.cacheProbe) {
    output.log(`${imageCacheProbePrefix}${cached === undefined ? 'miss' : 'hit'}`);
    return 0;
  }
  if (cached !== undefined) {
    output.log(`Ordinary image already present: ${reference} (${cached.Id})`);
    return 0;
  }

  runWithRetry('dotnet', [
    'run', path.join(options.repositoryRoot, 'eng', 'tools', 'verify-buildkit.cs'),
  ], { cwd: options.repositoryRoot });
  buildBakeTargets(options, sourceRevision, undefined, [target.bakeTarget], environmentSnapshot);
  const image = inspectImage(reference, options.repositoryRoot);
  validateImageInspection(image, reference, expectedLabels, 'Built ordinary image');
  output.log(`Built ordinary image: ${reference} (${image.Id})`);
  return 0;
}

export async function buildRuntimeCandidates(options, sourceRevision, operatorImages, images, wine, framework, operations = {}) {
  const resolveEnvironment = operations.resolveBakeEnvironmentSnapshot ??
    resolveBakeEnvironmentSnapshot;
  const startCandidate = operations.start ?? startWithRetry;
  const capabilityDefinitions = operations.capabilityDefinitions ?? options.capabilityDefinitions ?? defaultCapabilityDefinitions();
  const capabilityResources = operations.capabilityResources ?? { wine, framework };
  const snapshot = operations.environmentSnapshot ??
    resolveEnvironment(options, sourceRevision, operatorImages);
  const environment = createBakeChildEnvironment(
    snapshot,
    options,
    operations.parentEnvironment ?? process.env,
    operatorImages,
  );
  const tasks = images.map(image => ({
    label: `Runtime candidate '${image.producer.id}'`,
    run: async () => {
      const profileId = image.producer.id;
      const arguments_ = [path.join(options.repositoryRoot, 'eng', 'runtime-candidate-environment.mjs'), profileId];
      arguments_.push(...resolveRuntimeArguments(image, capabilityDefinitions, capabilityResources));
      arguments_.push('--');
      await startCandidate(process.execPath, arguments_, {
        cwd: options.repositoryRoot,
        env: environment,
      });
    },
  }));
  await runParallel(tasks, options.maximumParallel);
}

function validateFinalImageInspection(planned, image, plan, sourceRevision, allowDifferentSourceRevision = false) {
  const labels = image.Config?.Labels ?? {};
  if (!imageIdPattern.test(image.Id ?? '') || image.Os !== 'linux' || image.Architecture !== 'amd64') {
    fail(`Final image '${planned.id}' is not one immutable linux/amd64 image`);
  }
  if (labels[versionLabel] !== plan.releaseId) {
    fail(`Final image '${planned.id}' does not carry release label '${plan.releaseId}'`);
  }
  if (planned.producer.kind !== 'pull') {
    if (!sourceRevisionPattern.test(labels[sourceRevisionLabel] ?? '') || (!allowDifferentSourceRevision && labels[sourceRevisionLabel] !== sourceRevision)) fail(`Final image '${planned.id}' does not carry a valid source revision${allowDifferentSourceRevision ? '' : ` '${sourceRevision}'`}`);
    if (labels[developmentInputsLabel] !== 'true') fail(`Final image '${planned.id}' is missing the development image-input marker`);
  }
  return labels;
}

// A failed bundle must not invalidate images that were already built.  Keep
// this probe deliberately local: BuildKit owns layer reuse and a cache check
// must never turn into an implicit registry pull (especially on SSH sessions).
function finalImageAliases(planned) {
  if (planned.producer.kind === 'pull') return [planned.reference];
  const repository = imageRepository(planned.reference);
  const aliases = [planned.reference];
  if (planned.producer.kind === 'runtime-candidate') aliases.push(`${repository}:candidate`);
  aliases.push(`${repository}:content`);
  return aliases;
}

function tryReuseFinalImage(planned, plan, sourceRevision, repositoryRoot) {
  for (const reference of finalImageAliases(planned)) {
    const image = tryInspectImage(reference, repositoryRoot);
    if (image === undefined) continue;
    try {
      validateFinalImageInspection(planned, image, plan, sourceRevision, true);
    } catch {
      continue;
    }
    if (reference !== planned.reference) {
      runWithRetry('docker', ['image', 'tag', reference, planned.reference], { cwd: repositoryRoot });
    }
    return image;
  }
  return undefined;
}

function normalizeRebuildTarget(value) { return String(value ?? '').trim().toLowerCase(); }

export function matchesRebuildTarget(planned, selector) {
  const normalized = normalizeRebuildTarget(selector);
  const separator = normalized.indexOf(':');
  const namespace = separator > 0 ? normalized.slice(0, separator) : undefined;
  const target = separator > 0 ? normalized.slice(separator + 1) : normalized;
  if (target.length === 0) return false;
  const id = String(planned?.id ?? '').toLowerCase();
  const producer = String(planned?.producer?.id ?? '').toLowerCase();
  const namespacedValues = {
    capability: (planned?.buildCapabilities ?? []).map(value => String(value).toLowerCase()),
    feature: (planned?.buildCapabilities ?? []).map(value => String(value).toLowerCase()),
    runtime: [String(planned?.runtimeId ?? '').toLowerCase()],
    image: [id],
    producer: [producer],
    toolchain: [String(planned?.toolchainId ?? '').toLowerCase()],
    processor: [String(planned?.artifactProcessorId ?? '').toLowerCase()],
  };
  if (namespace !== undefined && !Object.hasOwn(namespacedValues, namespace)) return false;
  const values = namespace === undefined
    ? [...new Set(Object.values(namespacedValues).flat())]
    : namespacedValues[namespace];
  if (target.includes('*')) {
    const pattern = new RegExp(`^${target.split('*').map(value => value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')).join('.*')}$`);
    return values.some(value => pattern.test(value));
  }
  return values.some(value => value === target);
}

function validateRebuildTargets(options, plan) {
  const selectors = [...new Set((options.rebuildTargets ?? []).map(normalizeRebuildTarget).filter(Boolean))];
  const unmatched = selectors.filter(selector => !plan.images.some(image => matchesRebuildTarget(image, selector)));
  if (unmatched.length > 0) {
    const supported = [...new Set(plan.images.flatMap(image => [image.id, image.producer?.id, image.runtimeId, image.toolchainId, image.artifactProcessorId, ...(image.buildCapabilities ?? [])].filter(Boolean)))].sort().join(', ');
    fail(`Unknown --rebuild-target '${unmatched[0]}'. Available image, producer, component, and capability targets: ${supported}`);
  }
  options.rebuildTargets = selectors;
}

function isRebuildRequested(options, planned) { return (options.rebuildTargets ?? []).some(selector => matchesRebuildTarget(planned, selector)); }

function partitionPlannedImages(options, plan, sourceRevision, forceRebuild = false) {
  const cached = new Map();
  const missing = [];
  for (const planned of plan.images) {
    if (options.reuseExisting === false || forceRebuild || isRebuildRequested(options, planned)) {
      missing.push(planned);
      continue;
    }
    // Pull-only entries are independent of the repository source. Keep them
    // reusable even when a source-input fingerprint invalidates build outputs.
    if (planned.producer.kind === 'pull') {
      const image = tryInspectImage(planned.reference, options.repositoryRoot);
      if (image !== undefined) {
        try {
          validateFinalImageInspection(planned, image, plan, sourceRevision);
          cached.set(planned.id, image);
        } catch {
          missing.push(planned);
        }
      } else missing.push(planned);
      continue;
    }
    const image = tryReuseFinalImage(planned, plan, sourceRevision, options.repositoryRoot);
    if (image === undefined) missing.push(planned);
    else cached.set(planned.id, image);
  }
  return { cached, missing };
}

function verifyFinalImages(options, sourceRevision, plan, imagePlanDigest) {
  const result = [];
  for (const planned of plan.images) {
    const image = inspectImage(planned.reference, options.repositoryRoot);
    validateFinalImageInspection(planned, image, plan, sourceRevision, options.reuseExisting !== false);
    if (planned.producer.kind !== 'pull') {
      const contentAlias = `${imageRepository(planned.reference)}:content`;
      if (contentAlias !== planned.reference) {
        runWithRetry('docker', [
          'image', 'tag', planned.reference, contentAlias,
        ], { cwd: options.repositoryRoot });
      }
    }
    result.push({ id: planned.id, reference: planned.reference, imageId: image.Id, producer: planned.producer });
  }
  const output = path.join(options.repositoryRoot, 'artifacts', 'release-images.json');
  atomicWrite(output, `${JSON.stringify({
    schemaVersion: 2,
    releaseId: plan.releaseId,
    sourceRevision,
    imagePlanDigest: imagePlanDigest,
    images: result,
  }, null, 2)}\n`);
  return output;
}

function parseArguments(argv) {
  const result = {
    repositoryRoot: defaultRepositoryRoot,
    imagePrefix: 'sharplabnext',
    sourceRevision: undefined,
    maximumParallel: 5,
    acceptMicrosoftLicenses: false,
    offline: false,
    planOnly: false,
    cacheProbe: false,
    reuseExisting: true,
    target: 'gateway',
    all: false,
    targetSpecified: false,
    rebuildTargets: [],
  };
  for (let index = 0; index < argv.length; index++) {
    const argument = argv[index];
    if (argument === '--accept-microsoft-licenses') { result.acceptMicrosoftLicenses = true; continue; }
    if (argument === '--offline') { result.offline = true; continue; }
    if (argument === '--plan-only') { result.planOnly = true; continue; }
    if (argument === '--cache-probe') { result.cacheProbe = true; continue; }
    if (argument === '--no-reuse-existing') { result.reuseExisting = false; continue; }
    if (argument === '--rebuild-target') { const value = argv[++index]; if (value === undefined || value.length === 0) fail('--rebuild-target requires a value'); result.rebuildTargets.push(value); continue; }
    if (argument === '--all') {
      if (result.targetSpecified) fail('--target and --all cannot be used together');
      result.all = true;
      result.target = undefined;
      continue;
    }
    if (argument === '--help' || argument === '-h') return { help: true };
    const field = {
      '--repository-root': 'repositoryRoot',
      '--image-prefix': 'imagePrefix',
      '--source-revision': 'sourceRevision',
      '--max-parallel': 'maximumParallel',
      '--target': 'target',
    }[argument];
    if (field === undefined) fail(`Unknown build-images argument '${argument}'`);
    const value = argv[++index];
    if (value === undefined || value.length === 0) fail(`${argument} requires a value`);
    result[field] = field === 'maximumParallel' ? Number(value) : value;
    if (field === 'target') result.targetSpecified = true;
  }
  result.repositoryRoot = path.resolve(result.repositoryRoot);
  if (!fs.existsSync(path.join(result.repositoryRoot, 'SharpLabNext.slnx'))) fail('Repository root does not contain SharpLabNext.slnx');
  if (!/^[a-z0-9][a-z0-9._/-]{0,255}$/.test(result.imagePrefix) || result.imagePrefix.endsWith('/')) fail('--image-prefix is invalid');
  if (result.sourceRevision !== undefined && !sourceRevisionPattern.test(result.sourceRevision)) fail('--source-revision must be a 40- or 64-character source identity');
  if (!Number.isSafeInteger(result.maximumParallel) || result.maximumParallel < 1 || result.maximumParallel > 8) fail('--max-parallel must be an integer from 1 through 8');
  if (result.targetSpecified && result.all) fail('--target and --all cannot be used together');
  delete result.targetSpecified;
  return result;
}

function usage() {
  return `Usage: node eng/build-images.mjs [--repository-root PATH] [--image-prefix PREFIX]\n` +
    `  [--source-revision COMMIT] [--max-parallel 1..8] [--offline]\n` +
    `  [--target TARGET | --all]\n` +
    `  [--plan-only] [--cache-probe] [--no-reuse-existing] [--rebuild-target TARGET]\n` +
    `  [--accept-microsoft-licenses]\n` +
    `  (default target: gateway; default parallelism: 5; --all selects the complete image graph)`;
}

const capabilityProvisionerHandlers = Object.freeze({
  'wine-operator': async context => {
    const resource = buildWineOperator(context.options, context.sourceRevision, context.environmentSnapshot, context.needsFrameworkSeeds);
    return { resource, wine: resource };
  },
  'framework-matrix': async context => {
    const wineDigest = dependencyResource(context.definition, context.resources, 'digest');
    const operators = await buildFrameworkOperators(context.options, context.sourceRevision, wineDigest, context.prerequisiteState?.downloads, context.frameworkCandidates.length > 0 ? undefined : [], context.requiredSeedGenerations);
    const resource = context.frameworkCandidates.length > 0
      ? buildFrameworkControlImages(context.options, context.sourceRevision, wineDigest, operators, createFrameworkMatrixInput(context.options, operators))
      : operators;
    return { resource, operators };
  },
});

async function provisionCapabilityResources(options, sourceRevision, capabilities, definitions, prerequisiteState, prerequisiteManifest, environmentSnapshot, candidates, requiredSeedGenerations) {
  const providers = capabilityProvisioners(definitions, capabilities);
  const frameworkCandidates = imagesUsingProvisioner(candidates, definitions, 'framework-matrix');
  const needsFrameworkSeeds = frameworkCandidates.length > 0 || requiredSeedGenerations.size > 0;
  const resources = {};
  let wine = {};
  let operators = { seedReferences: new Map(), references: new Map() };
  for (const definition of providers) {
    const handler = capabilityProvisionerHandlers[definition.provisioner.kind];
    if (typeof handler !== 'function') fail(`Capability '${definition.id}' uses unsupported provisioner '${definition.provisioner.kind}'`);
    const result = await handler({ options, sourceRevision, definition, prerequisiteState, environmentSnapshot, resources, frameworkCandidates, requiredSeedGenerations, needsFrameworkSeeds });
    resources[definition.id] = result.resource;
    if (result.wine !== undefined) wine = result.wine;
    if (result.operators !== undefined) operators = result.operators;
  }
  if (needsFrameworkSeeds && operators.seedInputSha256 === undefined) fail('Framework seed provisioning requires a framework-matrix provisioner');
  const operatorDefinitions = requiredOperatorDefinitions(definitions, capabilities);
  const operatorImages = operatorDefinitions.length > 0
    ? await buildOperatorImages(options, prerequisiteState, operators, prerequisiteManifest, definitions, capabilities)
    : {};
  return { resources, wine, operators, operatorImages, frameworkCandidates };
}

export async function runBuildImages(argv, output = console) {
  const previousSourceIdentityMode = process.env[sourceIdentityModeEnvironmentVariable];
  try {
    const options = parseArguments(argv);
    if (options.help) { output.log(usage()); return 0; }
    // Source labels use a content identity; local reuse is controlled only by
    // explicit rebuild options and never requires Git metadata.
    options.sourceIdentityMode = contentSourceIdentityMode;
    process.env[sourceIdentityModeEnvironmentVariable] = contentSourceIdentityMode;
    const sourceRevision = resolveSourceRevision(options);
    options.resolvedSourceRevision = sourceRevision;
    if (options.target !== undefined) {
      return await runOrdinaryImageBuild(options, sourceRevision, output);
    }
    const imagePlan = generateImagePlan(options, sourceRevision);
    validateRebuildTargets(options, imagePlan.plan);
    options.releaseId = imagePlan.plan.releaseId;
    const counts = Object.fromEntries(['bake', 'runtime-candidate', 'pull'].map(kind => [kind, imagePlan.plan.images.filter(image => image.producer.kind === kind).length]));
    output.log(`Release image plan: ${imagePlan.plan.images.length} images (${counts.bake} Bake, ${counts['runtime-candidate']} runtime candidates, ${counts.pull} immutable pulls).`);
    if (options.planOnly) return 0;
    if (!options.acceptMicrosoftLicenses) fail('--accept-microsoft-licenses is required because the selected Catalog includes Microsoft proprietary runtime/toolchain inputs');

    const imageState = partitionPlannedImages(
      options,
      imagePlan.plan,
      sourceRevision,
      false,
    );
    output.log(`Image cache: ${imageState.cached.size} hit, ${imageState.missing.length} to build.`);
    if (options.cacheProbe) {
      const hit = imageState.missing.length === 0;
      output.log(`${imageCacheProbePrefix}${hit ? 'hit' : 'miss'}`);
      return 0;
    }
    if (imageState.missing.length === 0) {
      const validationPath = verifyFinalImages(options, sourceRevision, imagePlan.plan, imagePlan.digest);
      output.log(`All planned release images were already present and validated. Identity record: ${validationPath}`);
      return 0;
    }

    const missingIds = new Set(imageState.missing.map(image => image.id));
    const bakeTargets = [...new Set(imagePlan.plan.images.filter(image => image.producer.kind === 'bake' && missingIds.has(image.id)).map(image => image.producer.id))].sort();
    const candidates = imagePlan.plan.images.filter(image => image.producer.kind === 'runtime-candidate' && missingIds.has(image.id));
    options.capabilityDefinitions = imagePlan.capabilityDefinitions;
    const capabilities = resolveBuildCapabilities(imageState.missing, imagePlan.capabilityDefinitions);
    output.log(`Build capabilities: ${capabilities.size === 0 ? 'none' : [...capabilities].join(', ')}`);

    runWithRetry('dotnet', [
      'run', path.join(options.repositoryRoot, 'eng', 'tools', 'verify-buildkit.cs'),
    ], { cwd: options.repositoryRoot });
    verifyLocalImageBuildDriver(options.repositoryRoot);

    for (const image of imageState.missing.filter(image => image.producer.kind === 'pull')) {
      runWithRetry('docker', ['image', 'pull', image.reference], { cwd: options.repositoryRoot });
    }
    if (bakeTargets.length === 0 && candidates.length === 0) {
      const validationPath = verifyFinalImages(options, sourceRevision, imagePlan.plan, imagePlan.digest);
      output.log(`Fetched and validated every planned release image. Identity record: ${validationPath}`);
      return 0;
    }

    let prerequisiteState;
    const operatorDefinitions = requiredOperatorDefinitions(imagePlan.capabilityDefinitions, capabilities);
    const providers = capabilityProvisioners(imagePlan.capabilityDefinitions, capabilities);
    const requiredSeedGenerations = new Set([
      ...operatorDefinitions.map(definition => definition.operator?.frameworkSeedGeneration).filter(value => typeof value === 'string' && value.length > 0),
      ...providers.flatMap(definition => definition.provisioner.seedGenerations ?? []),
    ]);
    const needsRegistry = requiredSeedGenerations.size > 0 || providers.some(definition => definition.provisioner.requiresRegistry === true);
    const needsPrerequisites = operatorDefinitions.length > 0 || needsRegistry || providers.some(definition => definition.provisioner.requiresPrerequisites === true);
    const prerequisiteManifest = needsPrerequisites
      ? readPrerequisiteManifest(path.join(options.repositoryRoot, 'eng', 'release-prerequisites.json'))
      : undefined;
    if (needsPrerequisites) {
      const prerequisiteOutput = {
        logs: [],
        log(value) { this.logs.push(String(value)); output.log(value); },
        error(value) { output.error(value); },
      };
      const prerequisiteArguments = [
        'prepare', '--repository-root', options.repositoryRoot, '--accept-microsoft-licenses',
      ];
      if (options.offline) prerequisiteArguments.push('--offline');
      const prerequisiteStatus = await runPrerequisiteCache(prerequisiteArguments, prerequisiteOutput);
      if (prerequisiteStatus !== 0) fail('Prerequisite preparation failed');
      prerequisiteState = JSON.parse(prerequisiteOutput.logs.at(-1));
    }
    if (needsRegistry) {
      await ensureLocalRegistry(prerequisiteState?.localRegistry ?? prerequisiteManifest?.value.localRegistry, options.repositoryRoot);
    }

    // Resolve the lock/catalog environment once. Every later build receives
    // this immutable snapshot, avoiding repeated file-app compilation and temp
    // directory races between parallel candidates.
    const bakeEnvironmentSnapshot = resolveBakeEnvironmentSnapshot(
      options,
      sourceRevision,
      undefined,
    );
    const provisioned = await provisionCapabilityResources(options, sourceRevision, capabilities, imagePlan.capabilityDefinitions, prerequisiteState, prerequisiteManifest, bakeEnvironmentSnapshot, candidates, requiredSeedGenerations);

    buildBakeTargets(options, sourceRevision, provisioned.operatorImages, bakeTargets, bakeEnvironmentSnapshot);

    await buildRuntimeCandidates(options, sourceRevision, provisioned.operatorImages, candidates, provisioned.wine, provisioned.resources, { environmentSnapshot: bakeEnvironmentSnapshot, capabilityDefinitions: imagePlan.capabilityDefinitions, capabilityResources: provisioned.resources });
    const validationPath = verifyFinalImages(options, sourceRevision, imagePlan.plan, imagePlan.digest);
    output.log(`Built and validated every planned release image. Identity record: ${validationPath}`);
    return 0;
  } catch (error) {
    output.error(`Build images failed: ${error.message}`);
    return 1;
  } finally {
    if (previousSourceIdentityMode === undefined) {
      delete process.env[sourceIdentityModeEnvironmentVariable];
    } else {
      process.env[sourceIdentityModeEnvironmentVariable] = previousSourceIdentityMode;
    }
  }
}

if (process.argv[1] !== undefined && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = await runBuildImages(process.argv.slice(2));
}
