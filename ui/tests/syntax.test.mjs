// Every browser script must at least parse: a syntax error in one file blanks the whole app.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';

const dir = join(dirname(fileURLToPath(import.meta.url)), '..', 'js');

test('every ui/js script parses', () => {
  for (const name of readdirSync(dir).filter((f) => f.endsWith('.js'))) {
    assert.doesNotThrow(() => new vm.Script(readFileSync(join(dir, name), 'utf8'), { filename: name }), name);
  }
});
