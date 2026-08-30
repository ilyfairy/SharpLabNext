import { readdir, readFile, unlink, writeFile } from 'node:fs/promises';
import path from 'node:path';
import { brotliCompressSync, constants, gzipSync, zstdCompressSync } from 'node:zlib';

const distRoot = path.resolve(import.meta.dirname, '..', 'dist');
const minimumBytes = 256;
const minimumSavingsBytes = 32;
const minimumSavingsRatio = 0.01;
const compressibleExtensions = new Set(['.css', '.html', '.js', '.json', '.map', '.mjs', '.svg', '.txt', '.wasm', '.xml']);

const files = await listFiles(distRoot);
await Promise.all(files.filter((file) => file.endsWith('.br') || file.endsWith('.gz') || file.endsWith('.zst')).map((file) => unlink(file)));

const sourceFiles = files.filter((file) => !file.endsWith('.br') && !file.endsWith('.gz') && !file.endsWith('.zst') && compressibleExtensions.has(path.extname(file).toLowerCase()));

const totals = {
  identity: 0,
  gzip: 0,
  br: 0,
  zstd: 0,
};
let eligibleFiles = 0;
let smallFiles = 0;
let gzipFiles = 0;
let brotliFiles = 0;
let zstdFiles = 0;

for (const file of sourceFiles) {
  const source = await readFile(file);
  totals.identity += source.length;

  if (source.length < minimumBytes) {
    totals.gzip += source.length;
    totals.br += source.length;
    totals.zstd += source.length;
    smallFiles += 1;
    continue;
  }

  eligibleFiles += 1;
  const gzip = gzipSync(source, { level: 9 });
  const brotli = brotliCompressSync(source, {
    params: {
      [constants.BROTLI_PARAM_MODE]: isTextFile(file) ? constants.BROTLI_MODE_TEXT : constants.BROTLI_MODE_GENERIC,
      [constants.BROTLI_PARAM_QUALITY]: 11,
      [constants.BROTLI_PARAM_SIZE_HINT]: source.length,
    },
  });
  const zstd = zstdCompressSync(source, {
    params: {
      [constants.ZSTD_c_compressionLevel]: 19,
      [constants.ZSTD_c_checksumFlag]: 1,
    },
  });

  if (isWorthWriting(source.length, gzip.length)) {
    await writeFile(`${file}.gz`, gzip);
    totals.gzip += gzip.length;
    gzipFiles += 1;
  } else {
    totals.gzip += source.length;
  }

  if (isWorthWriting(source.length, brotli.length)) {
    await writeFile(`${file}.br`, brotli);
    totals.br += brotli.length;
    brotliFiles += 1;
  } else {
    totals.br += source.length;
  }

  if (isWorthWriting(source.length, zstd.length)) {
    await writeFile(`${file}.zst`, zstd);
    totals.zstd += zstd.length;
    zstdFiles += 1;
  } else {
    totals.zstd += source.length;
  }
}

console.log('\nFrontend precompression summary');
console.log(`  compressible assets: ${sourceFiles.length} (${eligibleFiles} eligible, ${smallFiles} below ${minimumBytes} B)`);
console.log('  encoding       variants       effective size       savings');
printSummary('identity', sourceFiles.length, totals.identity, totals.identity);
printSummary('gzip', gzipFiles, totals.gzip, totals.identity);
printSummary('brotli', brotliFiles, totals.br, totals.identity);
printSummary('zstd', zstdFiles, totals.zstd, totals.identity);

async function listFiles(directory) {
  const entries = await readdir(directory, { withFileTypes: true });
  const nested = await Promise.all(
    entries.map(async (entry) => {
      const entryPath = path.join(directory, entry.name);
      if (entry.isDirectory()) return listFiles(entryPath);
      if (!entry.isFile()) return [];
      return [entryPath];
    }),
  )
  return nested.flat();
}

function isTextFile(file) {
  return path.extname(file).toLowerCase() !== '.wasm';
}

function isWorthWriting(sourceBytes, compressedBytes) {
  const requiredSavings = Math.max(minimumSavingsBytes, Math.ceil(sourceBytes * minimumSavingsRatio));
  return compressedBytes <= sourceBytes - requiredSavings;
}

function printSummary(label, variants, bytes, identityBytes) {
  const savings = identityBytes === 0 ? 0 : (1 - bytes / identityBytes) * 100;
  console.log(`  ${label.padEnd(14)} ${String(variants).padStart(8)} ${formatBytes(bytes).padStart(20)} ${`${savings.toFixed(1)}%`.padStart(12)}`);
}

function formatBytes(bytes) {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KiB`;
  return `${(bytes / 1024 / 1024).toFixed(2)} MiB`;
}
