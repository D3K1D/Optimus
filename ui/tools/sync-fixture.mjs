// Copies contracts/fixtures/m1-simulated-audit.json to ui/fixtures/ (the fixture adapter fetches it from there).
// The two files must stay byte-identical: the host and the UI simulate the same dataset.
// Usage: node ui/tools/sync-fixture.mjs           copy
//        node ui/tools/sync-fixture.mjs --check   exit 1 if they differ
import { readFile, writeFile, mkdir } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const src = fileURLToPath(new URL('../../contracts/fixtures/m1-simulated-audit.json', import.meta.url));
const dst = fileURLToPath(new URL('../fixtures/m1-simulated-audit.json', import.meta.url));
const a = await readFile(src);
if (process.argv.includes('--check')) {
  let b = null;
  try { b = await readFile(dst); } catch { /* missing */ }
  if (!b || !a.equals(b)) { console.error('ui/fixtures/m1-simulated-audit.json differs from contracts/fixtures; run node ui/tools/sync-fixture.mjs'); process.exit(1); }
  console.log('fixture in sync');
} else {
  await mkdir(fileURLToPath(new URL('../fixtures/', import.meta.url)), { recursive: true });
  await writeFile(dst, a);
  console.log('copied ' + src + ' -> ' + dst);
}
