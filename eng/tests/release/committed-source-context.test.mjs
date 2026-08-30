import assert from 'node:assert/strict'
import { spawnSync } from 'node:child_process'
import fs from 'node:fs'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import { createCommittedSourceContext } from '../../committed-source-context.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..')
const revision = spawnSync(
  'git',
  ['rev-parse', '--verify', 'HEAD'],
  { cwd: repositoryRoot, encoding: 'utf8', shell: false },
).stdout.trim()

test('committed context contains exact Git bytes and excludes working-tree files', () => {
  const marker = path.join(repositoryRoot, '.tmp', `committed-context-untracked-${process.pid}-${Date.now()}.txt`)
  fs.mkdirSync(path.dirname(marker), { recursive: true })
  fs.writeFileSync(marker, 'must not enter committed context')
  let context
  try {
    context = createCommittedSourceContext({
      repositoryRoot,
      revision,
      requiredFiles: ['eng/bake.hcl', 'profiles/runtime-matrix.json'],
    })
    const committedBake = spawnSync(
      'git',
      ['show', `${revision}:eng/bake.hcl`],
      { cwd: repositoryRoot, encoding: null, shell: false },
    )
    assert.equal(committedBake.status, 0)
    assert.deepEqual(
      fs.readFileSync(path.join(context.directory, 'eng', 'bake.hcl')),
      committedBake.stdout,
    )
    assert.equal(
      fs.existsSync(path.join(context.directory, '.tmp', path.basename(marker))),
      false,
    )
    assert.deepEqual(fs.readdirSync(path.dirname(context.directory)), ['repository'])
  } finally {
    fs.rmSync(marker, { force: true })
    const root = context === undefined ? undefined : path.dirname(context.directory)
    context?.dispose()
    if (root !== undefined) assert.equal(fs.existsSync(root), false)
  }
})

test('committed context rejects unsafe paths and incomplete revisions', () => {
  assert.throws(() => createCommittedSourceContext({
    repositoryRoot,
    revision: 'a'.repeat(39),
    requiredFiles: ['eng/bake.hcl'],
  }), /full lowercase Git commit/)
  assert.throws(() => createCommittedSourceContext({
    repositoryRoot,
    revision,
    requiredFiles: ['../eng/bake.hcl'],
  }), /unsafe/)
})
