import crypto from 'node:crypto';
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { spawnSync } from 'node:child_process';
import { pathToFileURL } from 'node:url';

const commitPattern = /^(?:[0-9a-f]{40}|[0-9a-f]{64})$/;
const refPattern = /^refs\/sharplabnext\/runtime-matrix-snapshots\/[0-9A-Za-z._-]+$/;
const maximumGitOutput = 16 * 1024 * 1024;

export class RuntimeMatrixSnapshotError extends Error {}

function runGit(repositoryRoot, args, options = {}) {
  const result = spawnSync('git', ['-C', repositoryRoot, ...args], {
    encoding: 'utf8',
    env: { ...process.env, ...options.env },
    input: options.input,
    maxBuffer: maximumGitOutput,
    windowsHide: true,
  });
  if (result.error) {
    throw new RuntimeMatrixSnapshotError(`Could not run Git (${args.join(' ')}): ${result.error.message}`);
  };
  const allowed = options.allowedExitCodes ?? [0];
  if (!allowed.includes(result.status)) {
    const details = `${result.stderr ?? ''}\n${result.stdout ?? ''}`
      .trim()
      .replaceAll(/\s+/g, ' ')
      .slice(0, 4096);
    throw new RuntimeMatrixSnapshotError(`Git failed (${args.join(' ')})${details ? `: ${details}` : '.'}`);
  }
  return {
    status: result.status,
    stdout: result.stdout ?? '',
    stderr: result.stderr ?? '',
  }
}

function sha256File(filePath) { return crypto.createHash('sha256').update(fs.readFileSync(filePath)).digest('hex'); }

function normalizeRelative(root, candidate, label) {
  const relative = path.relative(root, candidate);
  if (!relative || relative === '..' || relative.startsWith(`..${path.sep}`) ||
      path.isAbsolute(relative)) {
    throw new RuntimeMatrixSnapshotError(`${label} must be a child of the repository root.`);
  }
  return relative;
}

function requireRegularParents(root, candidate) {
  const parent = path.dirname(candidate);
  if (parent === root) return;
  const relative = normalizeRelative(root, parent, 'Snapshot parent');
  let current = root;
  for (const segment of relative.split(path.sep)) {
    current = path.join(current, segment);
    if (!fs.existsSync(current)) break;
    const stat = fs.lstatSync(current);
    if (!stat.isDirectory() || stat.isSymbolicLink()) {
      throw new RuntimeMatrixSnapshotError(`Snapshot parent '${current}' must be a regular directory and cannot be a link.`);
    };
  }
}

function listSubmodulePaths(repositoryRoot) {
  if (!fs.existsSync(path.join(repositoryRoot, '.gitmodules'))) return [];
  const result = runGit(repositoryRoot, [
    'config', '--file', '.gitmodules', '--get-regexp', '^submodule\..*\.path$',
  ], { allowedExitCodes: [0, 1] });
  if (result.status === 1) return [];
  return result.stdout
    .split(/\r?\n/)
    .filter(Boolean)
    .map(line => line.slice(line.search(/\s/) + 1).trim());
}

function requireCleanSubmodules(repositoryRoot) {
  for (const relative of listSubmodulePaths(repositoryRoot)) {
    const submoduleRoot = path.resolve(repositoryRoot, relative);
    normalizeRelative(repositoryRoot, submoduleRoot, `Submodule '${relative}'`);
    if (!fs.existsSync(submoduleRoot)) {
      throw new RuntimeMatrixSnapshotError(`Submodule '${relative}' is not initialized.`);
    }
    const status = runGit(submoduleRoot, [
      'status', '--porcelain=v1', '--untracked-files=all', '--ignore-submodules=none',
    ]).stdout;
    if (status.length > 0) {
      throw new RuntimeMatrixSnapshotError(`Submodule '${relative}' must be clean before creating a snapshot.`);
    }
  }
}

function requireStableCapture(repositoryRoot, indexEnvironment) {
  const tracked = runGit(repositoryRoot, [
    'diff-files', '--quiet', '--ignore-submodules=none', '--', '.',
  ], { env: indexEnvironment, allowedExitCodes: [0, 1] });
  if (tracked.status !== 0) {
    throw new RuntimeMatrixSnapshotError('Repository files changed while the runtime matrix snapshot was being captured.');
  }
  const untracked = runGit(repositoryRoot, [
    'ls-files', '--others', '--exclude-standard', '-z', '--', '.',
  ], { env: indexEnvironment }).stdout;
  if (untracked.length > 0) {
    throw new RuntimeMatrixSnapshotError('New untracked files appeared while the runtime matrix snapshot was being captured.');
  }
  requireCleanSubmodules(repositoryRoot);
}

function resolveRepositoryRoot(value) {
  const requested = path.resolve(value);
  const actual = path.resolve(runGit(requested, ['rev-parse', '--show-toplevel']).stdout.trim());
  if (requested !== actual) {
    throw new RuntimeMatrixSnapshotError(`Repository root must be the Git top-level directory '${actual}'.`);
  }
  return actual;
}

function removeCreatedWorktree(repositoryRoot, outputPath) {
  runGit(repositoryRoot, ['worktree', 'remove', '--force', outputPath], {
    allowedExitCodes: [0, 1, 128],
  });
  if (fs.existsSync(outputPath)) fs.rmSync(outputPath, { recursive: true, force: true });
}

export function createRuntimeMatrixSnapshot(options, dependencies = {}) {
  const repositoryRoot = resolveRepositoryRoot(options.repositoryRoot ?? process.cwd());
  const outputPath = path.resolve(repositoryRoot, options.outputPath ?? '');
  const outputRelative = normalizeRelative(repositoryRoot, outputPath, 'Snapshot output');
  if (fs.existsSync(outputPath)) {
    throw new RuntimeMatrixSnapshotError(`Snapshot output '${outputPath}' already exists.`);
  }
  requireRegularParents(repositoryRoot, outputPath);
  const ignored = runGit(repositoryRoot, [
    'check-ignore', '--quiet', '--no-index', '--', outputRelative,
  ], { allowedExitCodes: [0, 1] });
  if (ignored.status !== 0) {
    throw new RuntimeMatrixSnapshotError(`Snapshot output '${outputRelative}' must be ignored by the repository.`);
  }
  requireCleanSubmodules(repositoryRoot);

  const head = runGit(repositoryRoot, ['rev-parse', '--verify', 'HEAD']).stdout.trim();
  if (!commitPattern.test(head)) {
    throw new RuntimeMatrixSnapshotError('The repository must have a full Git HEAD commit.');
  }
  const headTree = runGit(repositoryRoot, ['rev-parse', 'HEAD^{tree}']).stdout.trim();
  const objectFormat = runGit(repositoryRoot, ['rev-parse', '--show-object-format']).stdout.trim();
  if (objectFormat !== 'sha1' && objectFormat !== 'sha256') {
    throw new RuntimeMatrixSnapshotError(`Unsupported Git object format '${objectFormat}'.`);
  }
  const zeroObjectId = '0'.repeat(objectFormat === 'sha1' ? 40 : 64);
  const indexPath = path.resolve(runGit(repositoryRoot, ['rev-parse', '--path-format=absolute', '--git-path', 'index']).stdout.trim());
  const originalIndexDigest = fs.existsSync(indexPath) ? sha256File(indexPath) : null;
  const temporaryIndex = path.join(repositoryRoot, '.tmp', `.runtime-matrix-snapshot-${process.pid}-${crypto.randomBytes(8).toString('hex')}.index`);
  fs.mkdirSync(path.dirname(temporaryIndex), { recursive: true });
  const indexEnvironment = { GIT_INDEX_FILE: temporaryIndex };
  let snapshotRef;
  let refCreated = false;
  let worktreeCreated = false;

  try {
    runGit(repositoryRoot, ['read-tree', 'HEAD'], { env: indexEnvironment });
    runGit(repositoryRoot, ['add', '-A', '--', '.'], { env: indexEnvironment });
    runGit(repositoryRoot, ['diff', '--cached', '--check'], { env: indexEnvironment });
    dependencies.afterIndexCaptured?.();
    requireStableCapture(repositoryRoot, indexEnvironment);
    const tree = runGit(repositoryRoot, ['write-tree'], { env: indexEnvironment }).stdout.trim();
    let commit = head;
    if (tree !== headTree) {
      const commitEpoch = runGit(repositoryRoot, ['show', '-s', '--format=%ct', 'HEAD']).stdout.trim();
      const identityEnvironment = {
        ...indexEnvironment,
        GIT_AUTHOR_NAME: 'SharpLabNext Runtime Matrix',
        GIT_AUTHOR_EMAIL: 'runtime-matrix@invalid',
        GIT_COMMITTER_NAME: 'SharpLabNext Runtime Matrix',
        GIT_COMMITTER_EMAIL: 'runtime-matrix@invalid',
        GIT_AUTHOR_DATE: `@${commitEpoch} +0000`,
        GIT_COMMITTER_DATE: `@${commitEpoch} +0000`,
      };
      commit = runGit(repositoryRoot, ['commit-tree', tree, '-p', head], {
        env: identityEnvironment,
        input: 'Runtime matrix candidate snapshot\n',
      }).stdout.trim();
    }
    if (!commitPattern.test(commit)) {
      throw new RuntimeMatrixSnapshotError('Git did not produce a full snapshot commit.');
    }
    snapshotRef = options.snapshotRef ?? `refs/sharplabnext/runtime-matrix-snapshots/${commit}`;
    if (!refPattern.test(snapshotRef)) {
      throw new RuntimeMatrixSnapshotError(`Snapshot ref '${snapshotRef}' must stay below refs/sharplabnext/runtime-matrix-snapshots/.`);
    }
    const existing = runGit(repositoryRoot, ['rev-parse', '--verify', '--quiet', snapshotRef], {
      allowedExitCodes: [0, 1],
    });
    const existingCommit = existing.stdout.trim();
    if (existing.status === 0 && existingCommit !== commit) {
      throw new RuntimeMatrixSnapshotError(`Snapshot ref '${snapshotRef}' already points to a different commit.`);
    }
    if (existing.status === 1) {
      runGit(repositoryRoot, ['update-ref', snapshotRef, commit, zeroObjectId]);
      refCreated = true;
    }
    requireStableCapture(repositoryRoot, indexEnvironment);
    fs.mkdirSync(path.dirname(outputPath), { recursive: true });
    requireRegularParents(repositoryRoot, outputPath);
    dependencies.beforeWorktreeAdd?.();
    // A Windows checkout may have core.autocrlf=true globally.  The snapshot
    // is an immutable build input, so its worktree must expose the committed
    // bytes exactly; otherwise certificates, Dockerfiles, and scripts drift
    // to CRLF while Git still reports a clean tree.
    runGit(repositoryRoot, [
      '-c', 'core.autocrlf=false', 'worktree', 'add', '--detach', outputPath, commit,
    ]);
    worktreeCreated = true;
    if (listSubmodulePaths(repositoryRoot).length > 0) {
      runGit(outputPath, [
        '-c', 'core.autocrlf=false', 'submodule', 'update', '--init', '--recursive', '--checkout',
      ]);
    }
    const snapshotStatus = runGit(outputPath, [
      'status', '--porcelain=v1', '--untracked-files=all', '--ignore-submodules=none',
    ]).stdout;
    if (snapshotStatus.length > 0) {
      throw new RuntimeMatrixSnapshotError('The created snapshot worktree is not clean.');
    }
    const observedCommit = runGit(outputPath, ['rev-parse', 'HEAD']).stdout.trim();
    if (observedCommit !== commit) {
      throw new RuntimeMatrixSnapshotError('The created snapshot worktree has the wrong commit.');
    }
    requireStableCapture(repositoryRoot, indexEnvironment);
    const finalIndexDigest = fs.existsSync(indexPath) ? sha256File(indexPath) : null;
    if (finalIndexDigest !== originalIndexDigest) {
      throw new RuntimeMatrixSnapshotError('The main repository index changed during snapshot creation.');
    }
    return {
      repositoryRoot,
      outputPath,
      snapshotRef,
      baseRevision: head,
      snapshotRevision: commit,
      tree,
    }
  }
  catch (error) {
    if (worktreeCreated) removeCreatedWorktree(repositoryRoot, outputPath);
    if (refCreated && snapshotRef) {
      runGit(repositoryRoot, ['update-ref', '-d', snapshotRef], {
        allowedExitCodes: [0, 1, 128],
      });
    }
    throw error;
  }
  finally {
    fs.rmSync(temporaryIndex, { force: true });
  }
}

function parseArguments(args) {
  const options = { repositoryRoot: process.cwd() };
  for (let index = 0; index < args.length; index += 1) {
    const argument = args[index];
    const value = () => {
      index += 1;
      if (index >= args.length || args[index].startsWith('--')) {
        throw new RuntimeMatrixSnapshotError(`${argument} requires a value.`);
      }
      return args[index];
    }
    if (argument === '--repository-root') options.repositoryRoot = value();
    else if (argument === '--output') options.outputPath = value();
    else if (argument === '--ref') options.snapshotRef = value();
    else if (argument === '--help') options.help = true;
    else throw new RuntimeMatrixSnapshotError(`Unknown argument '${argument}'.`);
  }
  if (!options.help && !options.outputPath) {
    throw new RuntimeMatrixSnapshotError('--output is required.');
  }
  return options;
}

const usage = `Usage:
  node eng/create-runtime-matrix-snapshot.mjs \\
    --repository-root <path> \\
    --output <ignored-worktree-path> [--ref <refs/sharplabnext/runtime-matrix-snapshots/name>]
`;

async function main() {
  try {
    const options = parseArguments(process.argv.slice(2));
    if (options.help) {
      process.stdout.write(usage);
      return;
    }
    const result = createRuntimeMatrixSnapshot(options);
    process.stdout.write(`${JSON.stringify(result, null, 2)}\n`);
  }
  catch (error) {
    process.stderr.write(`${error.message}\n${usage}`);
    process.exitCode = 1
  }
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? '').href) await main();
