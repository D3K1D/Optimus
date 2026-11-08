// Lets `node --test ui/tests/` work on Node 22 (a directory argument resolves to this index): loads every *.test.mjs here.
const { readdirSync } = require('node:fs');
const { join } = require('node:path');
const { pathToFileURL } = require('node:url');
for (const f of readdirSync(__dirname).filter((n) => n.endsWith('.test.mjs')).sort()) import(pathToFileURL(join(__dirname, f)).href);
