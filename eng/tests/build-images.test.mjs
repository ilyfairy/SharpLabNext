import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import test from 'node:test';
import { fileURLToPath } from 'node:url';

import { validateJsonSchemaInstance } from '../release/json-schema-instance-validation.mjs';

import {
  buildRuntimeCandidates,
  applySourceVerificationMarker,
  createBakeChildEnvironment,
  parseBakeEnvironmentSnapshot,
  resolveOrdinaryBakeTarget,
  resolveBuildCapabilities,
  resolveRuntimeArguments,
  runParallel,
  validateReleaseImagePlan,
  validateLocalImageBuildDriverInspection,
  validateRegistryContainer,
  validateReusableImageInspection,
} from '../build-images.mjs';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const configuration = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'eng', 'release-prerequisites.json'), 'utf8')).localRegistry;
const capabilityDefinitions = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'deploy', 'images.json'), 'utf8')).capabilityDefinitions;

test('validated release image plans retain capability definitions as an array', () => {
  const plan = { schemaVersion: 1, releaseId: 'test', capabilityDefinitions: [{ id: 'base' }], images: [{ id: 'service', reference: 'example/service:test', producer: { kind: 'bake', id: 'service' }, buildCapabilities: ['base'] }] };
  const validatedDefinitions = validateReleaseImagePlan(plan);
  assert.ok(Array.isArray(validatedDefinitions));
  assert.deepEqual([...resolveBuildCapabilities(plan.images, validatedDefinitions)], ['base']);
});

test('build capabilities and runtime arguments resolve from generic definitions', () => {
  const image = { id: 'runtime-special-linux-x64', runtimeId: 'runtime-special-linux-x64', toolchainId: 'compiler-special', artifactProcessorId: 'processor-special', producer: { id: 'runtime-special' }, buildCapabilities: ['framework'] };
  assert.deepEqual([...resolveBuildCapabilities([image], capabilityDefinitions)].sort(), ['framework', 'wine']);
  assert.deepEqual([...resolveBuildCapabilities([{ buildCapabilities: ['custom-output'] }], [{ id: 'base' }, { id: 'custom-output', dependencies: ['base'] }])].sort(), ['base', 'custom-output']);
  assert.throws(() => resolveBuildCapabilities([{ buildCapabilities: ['unknown'] }], capabilityDefinitions), /Unknown build capability/);
  assert.throws(() => resolveBuildCapabilities([{ buildCapabilities: ['cycle-a'] }], [{ id: 'cycle-a', dependencies: ['cycle-b'] }, { id: 'cycle-b', dependencies: ['cycle-a'] }]), /dependency cycle/);
  const genericDefinitions = [
    { id: 'base', dependencies: [], runtimeArguments: [{ option: '--base-image', sourceCapability: 'base', output: 'image' }] },
    { id: 'combined', dependencies: ['base'], runtimeArguments: [{ option: '--extra-image', sourceCapability: 'base', output: 'image' }] },
  ];
  assert.deepEqual(resolveRuntimeArguments({ id: 'sample', buildCapabilities: ['base', 'combined'] }, genericDefinitions, { base: { image: 'registry.example/base@sha256:' + 'a'.repeat(64) } }), ['--base-image', 'registry.example/base@sha256:' + 'a'.repeat(64), '--extra-image', 'registry.example/base@sha256:' + 'a'.repeat(64)]);
  assert.throws(() => resolveRuntimeArguments({ id: 'sample', buildCapabilities: ['combined'] }, genericDefinitions, {}), /requires output/);
  assert.deepEqual(resolveRuntimeArguments({ id: 'sample', buildCapabilities: ['combined'] }, genericDefinitions, { base: { image: 'value' } }), ['--base-image', 'value', '--extra-image', 'value']);
  assert.throws(() => resolveRuntimeArguments({ id: 'sample', buildCapabilities: ['left', 'right'] }, [{ id: 'left', runtimeArguments: [{ option: '--same', sourceCapability: 'left', output: 'image' }] }, { id: 'right', runtimeArguments: [{ option: '--same', sourceCapability: 'right', output: 'image' }] }], { left: { image: 'one' }, right: { image: 'two' } }), /duplicate runtime option/);
  assert.deepEqual(resolveRuntimeArguments({ id: 'sample', buildCapabilities: ['combined', 'base'] }, genericDefinitions, { base: { image: 'value' } }), ['--base-image', 'value', '--extra-image', 'value']);
});

test('ordinary image mode resolves one standalone Bake target without a release plan', () => {
  const gateway = resolveOrdinaryBakeTarget('gateway');
  assert.deepEqual({ bakeTarget: gateway.bakeTarget, imageName: gateway.imageName, id: gateway.id }, { bakeTarget: 'gateway', imageName: 'gateway', id: 'gateway' });
  const dotnet = resolveOrdinaryBakeTarget('dotnet-10-linux-x64');
  assert.deepEqual({ bakeTarget: dotnet.bakeTarget, imageName: dotnet.imageName, id: dotnet.id, runtimeId: dotnet.runtimeId }, { bakeTarget: 'runtime-dotnet10', imageName: 'runtime-dotnet10', id: 'dotnet-10-linux-x64', runtimeId: 'dotnet-10-linux-x64' });
  assert.throws(() => resolveOrdinaryBakeTarget('worker-cppcli'), /not a standalone ordinary image target/);
  assert.throws(() => resolveOrdinaryBakeTarget('toString'), /not a standalone ordinary image target/);
  const deployment = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'deploy', 'images.json'), 'utf8'));
  assert.equal(deployment.images.find(image => image.id === 'dotnet-10-linux-x64').ordinaryBakeTarget, 'runtime-dotnet10');
  const frameworkRuntimes = deployment.images.filter(image => image.id.startsWith('wine-netfx'));
  assert.ok(frameworkRuntimes.length > 0);
  for (const image of frameworkRuntimes) assert.deepEqual(image.buildCapabilities, ['framework'], `framework capability for ${image.id}`);
});

test('deployment image schema distinguishes generated and immutable pull producers', () => {
  const schema = JSON.parse(fs.readFileSync(path.join(repositoryRoot, 'schemas', 'deployment-images.schema.json'), 'utf8'));
  const immutableReference = `registry.example/runtime@sha256:${'a'.repeat(64)}`;
  const pull = { schemaVersion: 1, images: [{ id: 'promoted-runtime', repository: 'registry.example/runtime', runtimeId: 'promoted-runtime', immutableReference, producer: { kind: 'pull', id: immutableReference } }] };
  const unsafeBake = { schemaVersion: 1, images: [{ id: 'service', repository: 'registry.example/service', always: true, producer: { kind: 'bake', id: 'unsafe/target' } }] };
  assert.deepEqual(validateJsonSchemaInstance(pull, schema), []);
  assert.notDeepEqual(validateJsonSchemaInstance(unsafeBake, schema), []);
});

function container() {
  return {
    Image: configuration.imageId,
    Config: { Image: configuration.image },
    HostConfig: {
      RestartPolicy: { Name: 'unless-stopped' },
      PortBindings: {
        '5000/tcp': [{
          HostIp: configuration.host,
          HostPort: String(configuration.port),
        }],
      },
    },
    State: { Running: true },
  }
}

test('managed release registry is bound to one pinned image and loopback port', () => {
  assert.doesNotThrow(() => validateRegistryContainer(container(), configuration));

  const compatibleExisting = container();
  compatibleExisting.HostConfig.RestartPolicy.Name = 'no';
  assert.doesNotThrow(() => validateRegistryContainer(compatibleExisting, configuration, false));
  assert.throws(() => validateRegistryContainer(compatibleExisting, configuration), /restart policy/);

  const wrongImage = container();
  wrongImage.Image = `sha256:${'f'.repeat(64)}`;
  assert.throws(() => validateRegistryContainer(wrongImage, configuration), /does not match the pinned release registry/);

  const publicPort = container();
  publicPort.HostConfig.PortBindings['5000/tcp'][0].HostIp = '0.0.0.0';
  assert.throws(() => validateRegistryContainer(publicPort, configuration), /must bind only 127\.0\.0\.1:5000/);
});

test('complete image build requires the host-image-aware Docker driver', () => {
  assert.doesNotThrow(() => validateLocalImageBuildDriverInspection('Name: default\r\nDriver: docker\r\nBuildKit version: v0.31.1\r\n'));
  assert.throws(() => validateLocalImageBuildDriverInspection('Driver: docker-container\n'), /observed 'docker-container'/);
  assert.throws(() => validateLocalImageBuildDriverInspection('BuildKit version: v0.31.1\n'), /observed '<unknown>'/);
});

test('prerequisite image reuse requires exact labels and one immutable digest', () => {
  const reference = 'localhost:5000/sharplabnext-content/cache:input-test';
  const repository = 'localhost:5000/sharplabnext-content/cache';
  const digest = `${repository}@sha256:${'a'.repeat(64)}`;
  const image = {
    Id: `sha256:${'b'.repeat(64)}`,
    Os: 'linux',
    Architecture: 'amd64',
    RepoDigests: [digest],
    Config: { Labels: { expected: 'value' } },
  }

  assert.equal(validateReusableImageInspection(image, reference, { expected: 'value' }), digest);
  assert.throws(() => validateReusableImageInspection(image, reference, { expected: 'other' }), /label 'expected'/);
  assert.throws(
    () => validateReusableImageInspection(
      { ...image, RepoDigests: [] },
      reference,
      { expected: 'value' },
    ),
    /unique immutable RepoDigest/,
  );
});

test('parallel image build failures identify the exact target', async () => {
  await assert.rejects(
    runParallel([
      { label: "Framework operator 'netfx20'", run: async () => {} },
      {
        label: "Framework operator 'netfx30'",
        run: async () => { throw new Error("'dotnet' exited 1") },
      },
    ], 2),
    /Framework operator 'netfx30' failed: 'dotnet' exited 1/,
  );
});

test('Bake environment snapshot is structured', () => {
  const snapshot = parseBakeEnvironmentSnapshot(
    'ILSense inputs valid.\n' +
    'SHARPLABNEXT_BAKE_ENVIRONMENT_JSON={"IMAGE_PREFIX":"registry.example/app","RELEASE_ID":"content"}\n',
  );
  assert.deepEqual(snapshot, {
    IMAGE_PREFIX: 'registry.example/app',
    RELEASE_ID: 'content',
  });
  assert.throws(() => parseBakeEnvironmentSnapshot('SHARPLABNEXT_BAKE_ENVIRONMENT_JSON=[]\n'), /must be an object/);
});

test('candidate child environment preserves the trusted snapshot and adds operator inputs', () => {
  const inherited = {
    Path: 'host-path',
  };
  const clean = createBakeChildEnvironment(
    { IMAGE_PREFIX: 'registry.example/app' },
    {},
    inherited,
  );
  assert.equal(clean.Path, 'host-path');
  assert.equal(clean.IMAGE_PREFIX, 'registry.example/app');

  const operatorEnvironment = createBakeChildEnvironment(
    { IMAGE_PREFIX: 'registry.example/app' },
    { capabilityDefinitions },
    {},
    { 'jsharp20-toolchain-base': 'registry.example/jsharp@sha256:' + 'a'.repeat(64) },
  );
  assert.equal(operatorEnvironment.JSHARP_TOOLCHAIN_IMAGE, 'registry.example/jsharp@sha256:' + 'a'.repeat(64));
});

test('source verification marker is validated without changing ordinary content mode', () => {
  const clean = {};
  assert.equal(applySourceVerificationMarker(clean, 'SHARPLABNEXT_SOURCE_VERIFIED=true\n'), 'true');

  const content = {};
  assert.equal(applySourceVerificationMarker(content, 'SHARPLABNEXT_SOURCE_VERIFIED=false\n'), 'false');
  assert.throws(
    () => applySourceVerificationMarker({}, 'SHARPLABNEXT_SOURCE_VERIFIED=maybe\n'),
    /invalid verification marker/,
  );
});

test('runtime candidates resolve Bake environment once and still build in parallel', async () => {
  const options = {
    repositoryRoot,
    maximumParallel: 2,
    sourceIdentityMode: 'content',
  };
  const images = [
    { producer: { id: 'dotnet-10-linux-x64' }, buildCapabilities: [] },
    { producer: { id: 'wine-dotnet-10-linux-x64' }, buildCapabilities: ['wine'] },
    { producer: { id: 'wine-netfx48-linux-x64' }, buildCapabilities: ['framework'] },
  ];
  let resolutions = 0;
  let active = 0;
  let maximumActive = 0;
  const starts = [];
  await buildRuntimeCandidates(
    options,
    'f'.repeat(40),
    { 'jsharp20-toolchain-base': 'unused' },
    images,
    { localTag: 'sharplabnext/operator-wine-coreclr:content', digest: 'wine-digest' },
    { candidateInput: 'framework-input.json' },
    {
      parentEnvironment: { Path: 'host-path' },
      resolveBakeEnvironmentSnapshot() {
        resolutions++;
        return { IMAGE_PREFIX: 'sharplabnext', RELEASE_ID: 'content' }
      },
      async start(command, arguments_, startOptions) {
        starts.push({ command, arguments_, startOptions })
        active++;
        maximumActive = Math.max(maximumActive, active)
        await new Promise(resolve => setImmediate(resolve))
        active--;
      },
    },
  );

  assert.equal(resolutions, 1)
  assert.equal(starts.length, images.length)
  assert.equal(maximumActive, options.maximumParallel)
  assert.ok(starts.every(call => call.command === process.execPath))
  assert.ok(starts.every(call => call.startOptions.cwd === repositoryRoot))
  assert.ok(starts.every(call => call.startOptions.env.IMAGE_PREFIX === 'sharplabnext'))
  assert.ok(starts.every(call => call.startOptions.env.SHARPLABNEXT_SOURCE_IDENTITY_MODE === 'content'));
  assert.ok(starts.some(call => call.arguments_.includes('--wine-image')))
  assert.ok(starts.some(call => call.arguments_.includes('--framework-input')))
});

test('generated operator images are deferred while Bake parses earlier targets', () => {
  const bake = fs.readFileSync(path.join(repositoryRoot, 'eng', 'bake.hcl'), 'utf8')
  assert.match(bake, /function "deferred_image" \{\s+params = \[value\]\s+result = value != "" \? value : "scratch"\s+\}/)
  for (const [name, consumers] of [
    ['CPPCLI_PREPARED_BASE_IMAGE', 2],
    ['JSHARP_TOOLCHAIN_IMAGE', 2],
  ]) {
    assert.equal(bake.match(new RegExp(`deferred_image\\(${name}\\)`, 'g'))?.length, consumers)
    assert.doesNotMatch(bake, new RegExp(`required\\(${name}\\)`))
  }
  assert.equal(bake.includes('JSHARP_WINE_BASE_IMAGE'), false)
  assert.equal(bake.match(/"jsharp-wine-base(?:-context)?" = "target:jsharp-wine-base"/g)?.length, 2)
});

test('complete image build keeps installers out of host execution', () => {
  const orchestrator = fs.readFileSync(path.join(repositoryRoot, 'eng', 'build-images.mjs'), 'utf8');
  const preparation = fs.readFileSync(path.join(repositoryRoot, 'eng', 'tools', 'prepare-framework-runtime.cs'), 'utf8');
  assert.match(orchestrator, /--installer-secret-file/);
  assert.match(orchestrator, /--cached-winetricks-payload-file/);
  assert.match(orchestrator, /cachedWinetricksPayloads/);
  assert.match(orchestrator, /'build', preparationScript, '--nologo'/);
  assert.match(orchestrator, /'run', preparationScript, '--no-build', '--'/);
  assert.match(orchestrator, /'--build-kind', 'wow64-base'/);
  assert.match(orchestrator, /'--build-kind', 'companion-seed'/);
  assert.match(orchestrator, /createFrameworkSeedBuildSpec/);
  assert.match(orchestrator, /createOperatorImageBuildSpec/);
  assert.match(orchestrator, /buildOperatorImages/);
  assert.match(orchestrator, /requiredOperatorDefinitions/);
  assert.ok(orchestrator.indexOf('buildFrameworkOperators') < orchestrator.indexOf('buildOperatorImages'));
  assert.ok(orchestrator.indexOf('buildOperatorImages') < orchestrator.indexOf('buildBakeTargets'));
  assert.match(orchestrator, /maximumParallel: 5/);
  assert.match(orchestrator, /runParallel\(seedTasks, options\.maximumParallel\)/);
  assert.match(orchestrator, /runParallel\(tasks, options\.maximumParallel\)/);
  assert.match(orchestrator, /verify-buildkit\.cs/);
  assert.match(orchestrator, /requires the Docker Buildx driver/);
  assert.doesNotMatch(orchestrator, /Start-Process|\.InstallerPath\s*\)/);
  assert.match(preparation, /Process\.Start\(startInfo\)/);
  assert.match(preparation, /FileName = command\[0\]|ProcessStartInfo\(invocation\.Command\)/)
  assert.match(preparation, /new\("buildx"\)/)
  assert.match(preparation, /"check-attr",\s*"filter"/)
  assert.match(preparation, /unexpanded Git LFS pointer/)
  assert.match(preparation, /framework-vendored-context/)
  assert.match(preparation, /framework-cached-context/)
  assert.match(preparation, /framework-installer-context/)
  assert.match(preparation, /--build-context/)
  assert.match(preparation, /"FRAMEWORK_INSTALLER_NETWORK"[\s\S]+inputs\.CachedPayload is null \? "default" : "none"/)
  assert.doesNotMatch(preparation, /new\("--network"\)/)
  assert.match(preparation, /FRAMEWORK_SEED_IMAGE/)
  assert.match(preparation, /framework-wow64-base/)
  assert.match(preparation, /framework-companion-seed/)
});

test('build, image build, bundle, and release entry points keep distinct responsibilities', () => {
  const imageOrchestrator = fs.readFileSync(path.join(repositoryRoot, 'eng', 'build-images.mjs'), 'utf8');
  assert.match(imageOrchestrator, /target: 'gateway'/)
  assert.match(imageOrchestrator, /argument === '--all'/)
  assert.match(imageOrchestrator, /runOrdinaryImageBuild/)
  for (const extension of ['ps1', 'sh']) {
    const hostBuild = fs.readFileSync(path.join(repositoryRoot, 'eng', `build.${extension}`), 'utf8');
    const bundle = fs.readFileSync(path.join(repositoryRoot, 'eng', `bundle.${extension}`), 'utf8');
    const release = fs.readFileSync(path.join(repositoryRoot, 'eng', `release.${extension}`), 'utf8');
    assert.doesNotMatch(hostBuild, /Test-Path[^\n]*\.git|\[\[.*\.git/)
    assert.doesNotMatch(bundle, /buildx|dotnet restore|npm (?:ci|install)/)
    assert.ok(release.includes(`build.${extension}`))
    assert.match(release, /build-images/)
    assert.match(release, /bundle/)
    assert.match(release, extension === 'ps1' ? /All\s*=\s*\$true/ : /build_arguments=\(--all\)/)
    assert.ok(release.lastIndexOf(`build.${extension}`) < release.lastIndexOf(`build-images.${extension}`))
    assert.ok(release.lastIndexOf(`build-images.${extension}`) < release.lastIndexOf(`bundle.${extension}`))
  }
});

test('PowerShell release entry forwards child script parameters by name', () => {
  const release = fs.readFileSync(path.join(repositoryRoot, 'eng', 'release.ps1'), 'utf8');
  assert.match(release, /\$buildArguments\s*=\s*@\{/)
  assert.match(release, /\$bundleArguments\s*=\s*@\{/)
  assert.doesNotMatch(release, /\$buildArguments\s*=\s*@\(/)
  assert.doesNotMatch(release, /\$bundleArguments\s*=\s*@\(/)
});

test('file-based C# directives keep the separator after the directive name', () => {
  const roots = ['eng/tools', 'eng/performance', 'eng/smoke'];
  const files = [];
  const visit = directory => {
    for (const entry of fs.readdirSync(path.join(repositoryRoot, directory), { withFileTypes: true })) {
      const relative = path.join(directory, entry.name);
      if (entry.isDirectory() && !['bin', 'obj'].includes(entry.name)) visit(relative);
      else if (entry.name.endsWith('.cs')) files.push(relative);
    }
  }
  for (const root of roots) visit(root);
  for (const relative of files) {
    const source = fs.readFileSync(path.join(repositoryRoot, relative), 'utf8');
    assert.doesNotMatch(source, /^#:(?:sdk|project|package|property|using|load|import)\S/m, `${relative} has a malformed file-based directive`)
  }
});

test('full Compose E2E uses the complete release entry on a trusted private runner', () => {
  const workflow = fs.readFileSync(path.join(repositoryRoot, '.github', 'workflows', 'ci.yml'), 'utf8');
  assert.match(workflow, /vars\.SHARPLABNEXT_PRIVATE_E2E_ENABLED == 'true'/);
  assert.match(workflow, /runs-on: \[self-hosted, Linux, X64, sharplabnext-private\]/);
  assert.match(workflow, /bash \.\/eng\/release\.sh/);
  assert.equal(workflow.match(/^\s+lfs: true$/gm)?.length, 3);
  const profileWorkflow = fs.readFileSync(path.join(repositoryRoot, '.github', 'workflows', 'profile-update.yml'), 'utf8');
  assert.match(profileWorkflow, /^\s+lfs: true$/m);
  assert.doesNotMatch(workflow, /docker buildx prune/);
});
