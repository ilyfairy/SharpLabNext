import assert from 'node:assert/strict'
import crypto from 'node:crypto'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import test from 'node:test'
import { fileURLToPath } from 'node:url'

import {
  refreshRuntimeFunctionalMatrix,
  runRuntimeFunctionalMatrix,
} from '../smoke/runtime-functional-matrix.mjs'

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const matrixPath = path.join(repositoryRoot, 'profiles', 'runtime-matrix.json')
const candidateDirectory = path.join(repositoryRoot, 'profiles', 'runtimes', 'candidates')

function imageId(reference, suffix = '') { return `sha256:${crypto.createHash('sha256').update(`${reference}:${suffix}`).digest('hex')}`; }

function inspection(reference, suffix = '') {
  return {
    imageId: imageId(reference, suffix),
    sizeBytes: 1024,
    operatingSystem: 'linux',
    architecture: 'amd64',
    repoDigests: [],
    labels: { 'com.sharplabnext.runtime-profile': reference.split('/').at(-1).split(':')[0] },
  }
}

function temporaryResult(t) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'sharplabnext-functional-matrix-'))
  t.after(() => fs.rmSync(directory, { recursive: true, force: true }))
  return path.join(directory, 'results.json')
}

test('inventory derives the canonical 34 rows and all shared candidate boundaries', t => {
  const outputPath = temporaryResult(t)
  const result = refreshRuntimeFunctionalMatrix({
    matrixPath,
    candidateDirectory,
    outputPath,
    inspect: reference => inspection(reference),
    now: () => new Date('2026-08-13T00:00:00.000Z'),
  })

  assert.equal(result.rows.length, 34)
  assert.equal(new Set(result.rows.map(row => row.profileId)).size, 34)
  assert.equal(result.rows.some(row => row.profileId === 'wine-dotnet-core-2.0-linux-x64'), false)
  assert.deepEqual(
    Object.fromEntries(Object.entries(Object.groupBy(result.rows, row => row.candidateTarget))
      .map(([target, rows]) => [target, rows.length])),
    {
      'runtime-dotnet-matrix-candidate': 12,
      'runtime-wine-dotnet-matrix-candidate': 7,
      'runtime-mono-matrix-candidate': 1,
      'runtime-wine-framework-matrix-shared-candidate': 14,
    },
  )
  assert.equal(
    result.rows.find(row => row.profileId === 'dotnet-core-2.0-linux-x64')
      .expected.sourceMappingKind,
    'none',
  )
  assert.equal(
    result.rows.find(row => row.profileId === 'dotnet-10-linux-x64')
      .expected.sourceMappingKind,
    'linux-profiler',
  )
  assert.equal(result.rows.every(row => row.verification.status === 'unverified'), true)
  assert.equal(JSON.parse(fs.readFileSync(outputPath, 'utf8')).rows.length, 34)
})

test('verification survives only an unchanged profile digest and non-null image ID', t => {
  const outputPath = temporaryResult(t)
  const initial = refreshRuntimeFunctionalMatrix({
    matrixPath,
    candidateDirectory,
    outputPath,
    inspect: reference => inspection(reference),
  })
  initial.rows[0].verification = {
    status: 'smoke-passed',
    evidence: { stdout: 'expected' },
  }
  fs.writeFileSync(outputPath, `${JSON.stringify(initial, null, 2)}\n`)

  const preserved = refreshRuntimeFunctionalMatrix({
    matrixPath,
    candidateDirectory,
    outputPath,
    inspect: reference => inspection(reference),
  })
  assert.equal(preserved.rows[0].verification.status, 'smoke-passed')
  assert.equal(preserved.rows[0].verification.evidence.stdout, 'expected')

  const changedImage = refreshRuntimeFunctionalMatrix({
    matrixPath,
    candidateDirectory,
    outputPath,
    inspect: reference => inspection(reference, 'changed'),
  })
  assert.equal(changedImage.rows[0].verification.status, 'unverified')
  assert.equal(changedImage.rows[0].verification.reason, 'candidate-image-changed')

  changedImage.rows[0].verification = { status: 'smoke-passed' }
  fs.writeFileSync(outputPath, `${JSON.stringify(changedImage, null, 2)}\n`)
  const unavailable = refreshRuntimeFunctionalMatrix({
    matrixPath,
    candidateDirectory,
    outputPath,
    inspect() { throw new Error('image is absent') },
  })
  assert.equal(unavailable.rows[0].image.imageId, null)
  assert.equal(unavailable.rows[0].verification.status, 'unverified')
  assert.equal(unavailable.rows[0].verification.reason, 'candidate-image-unavailable')
})

test('a profile byte change invalidates prior verification', t => {
  const outputPath = temporaryResult(t)
  const copiedProfiles = path.join(path.dirname(outputPath), 'profiles')
  fs.cpSync(candidateDirectory, copiedProfiles, { recursive: true })
  const initial = refreshRuntimeFunctionalMatrix({
    matrixPath,
    candidateDirectory: copiedProfiles,
    outputPath,
    inspect: reference => inspection(reference),
  })
  const selected = initial.rows.find(row => row.profileId === 'dotnet-core-2.0-linux-x64')
  selected.verification = { status: 'smoke-passed' }
  fs.writeFileSync(outputPath, `${JSON.stringify(initial, null, 2)}\n`)
  fs.appendFileSync(path.join(copiedProfiles, `${selected.profileId}.json`), '\n')

  const changed = refreshRuntimeFunctionalMatrix({
    matrixPath,
    candidateDirectory: copiedProfiles,
    outputPath,
    inspect: reference => inspection(reference),
  })
  const row = changed.rows.find(value => value.profileId === selected.profileId)
  assert.equal(row.verification.status, 'unverified')
  assert.equal(row.verification.reason, 'profile-changed')
})

test('malformed prior state fails without overwriting it', t => {
  const outputPath = temporaryResult(t)
  const invalid = '{"schemaVersion":999,"rows":[]}'
  fs.writeFileSync(outputPath, invalid)
  assert.throws(
    () => refreshRuntimeFunctionalMatrix({
      matrixPath,
      candidateDirectory,
      outputPath,
      inspect: reference => inspection(reference),
    }),
    /Previous functional result.*schema version/,
  )
  assert.equal(fs.readFileSync(outputPath, 'utf8'), invalid)
})

test('CLI reports a compact summary', t => {
  const outputPath = temporaryResult(t)
  const output = {
    logs: [],
    errors: [],
    log(value) { this.logs.push(value) },
    error(value) { this.errors.push(value) },
  }
  assert.equal(runRuntimeFunctionalMatrix(['--output', outputPath], {
    matrixPath,
    candidateDirectory,
    inspect: reference => inspection(reference),
    output,
  }), 0)
  assert.deepEqual(output.errors, [])
  assert.match(output.logs[0], /34 runtime rows: 34 local images/)
})
