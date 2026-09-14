import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const demo = await readFile(new URL('../docs/clinic-demo.js', import.meta.url), 'utf8');

test('every dropdown in the clinic demo has a name a screen reader can read', () => {
  const ids = [...demo.matchAll(/<select id="([^"]+)"([^>]*)>/g)];
  assert.ok(ids.length > 0, 'the demo lost its dropdowns');
  for (const [, id, attrs] of ids) {
    const named = /aria-label="[^"]+"/.test(attrs) || demo.includes(`<label for="${id}">`);
    assert.ok(named, `#${id} has no label`);
  }
});
