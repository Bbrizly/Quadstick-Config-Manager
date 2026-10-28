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

test('client section selectors expose their group and selected state', () => {
  assert.match(demo, /class="c-tabs" role="group" aria-label="Client record sections"/);
  assert.match(demo, /data-tab="\$\{k\}" aria-pressed="\$\{state\.tab===k\}"/);
});

test('muted demo copy uses a WCAG AA contrast color on light panels', () => {
  const faint = demo.match(/--c-faint:(#[\da-f]{6})/i)?.[1];
  assert.ok(faint, 'missing muted text token');
  const luminance = hex => {
    const [r, g, b] = [1, 3, 5].map(i => parseInt(hex.slice(i, i + 2), 16) / 255)
      .map(c => c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
    return 0.2126 * r + 0.7152 * g + 0.0722 * b;
  };
  const ratio = (a, b) => {
    const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
    return (hi + 0.05) / (lo + 0.05);
  };
  assert.ok(ratio(faint, '#ffffff') >= 4.5, 'muted text fails AA on a white panel');
  for (const color of ['#6d7a75', '#788680', '#6f8179', '#718079']) {
    assert.doesNotMatch(demo, new RegExp(color, 'i'), `${color} is too faint for 12px panel text`);
  }
});
