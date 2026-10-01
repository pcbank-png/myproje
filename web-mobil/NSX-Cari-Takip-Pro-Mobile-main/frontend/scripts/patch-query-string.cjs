// decode-uri-component 0.5 fixes malformed-input exponential decoding and is
// ESM. query-string 7 expects a CommonJS function; adapt its import at install.
const fs = require('node:fs');
const path = require('node:path');
const source = require.resolve('query-string');
const old = "const decodeComponent = require('decode-uri-component');";
const fixed = "const decodeModule = require('decode-uri-component');\nconst decodeComponent = decodeModule.default ?? decodeModule;";
const text = fs.readFileSync(source, 'utf8');
if (text.includes(old)) fs.writeFileSync(source, text.replace(old, fixed));
else if (!text.includes(fixed)) throw new Error('query-string import changed; review decoder compatibility.');
console.log('Patched decoder compatibility:', path.basename(path.dirname(source)));
