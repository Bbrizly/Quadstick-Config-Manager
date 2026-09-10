import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const page = await readFile(new URL('../docs/clinic.html', import.meta.url), 'utf8');
const homepage = await readFile(new URL('../docs/index.html', import.meta.url), 'utf8');
const quotes = await readFile(new URL('../docs/testimonials.js', import.meta.url), 'utf8');
const pages = [['clinic.html', page], ['index.html', homepage]];

test('both pages carry the same audience switch, each marking its own side', () => {
  for (const [name, text] of pages) {
    assert.match(text, /data-audience-switch/, `${name} has no switch`);
    assert.match(text, /href="index\.html"[^>]*data-audience="individual"/, `${name} cannot reach the free app`);
    assert.match(text, /href="clinic\.html"[^>]*data-audience="clinician"/, `${name} cannot reach Clinic`);
  }
  assert.match(page, /data-audience="clinician" aria-current="page"/);
  assert.match(homepage, /data-audience="individual" aria-current="page"/);
});

test('each page prefetches the other, so the switch is instant', () => {
  assert.match(page, /rel="prefetch" href="index\.html"/);
  assert.match(homepage, /rel="prefetch" href="clinic\.html"/);
});

test('clinician page explains the roster, history, and capability model', () => {
  for (const phrase of [
    'One record per client, across every visit.',
    'Client history',
    'Capabilities',
    'profile snapshots',
    'private clinic folder',
  ]) {
    assert.match(page, new RegExp(phrase, 'i'), `missing product promise: ${phrase}`);
  }
});

test('the workspace preview is a roster you can click through', () => {
  assert.match(page, /const CLIENTS = \[/);
  assert.match(page, /addEventListener\('click', \(\) => show\(client\)\)/);
  assert.match(page, /aria-pressed/);
  assert.match(page, /id="client-panel" aria-live="polite"/);
  assert.equal((page.match(/name:'/g) ?? []).length, 4, 'the preview roster lost a client');
});

test('clinician page has a usable demo request form with required fields', () => {
  assert.match(page, /<form[^>]+id="demo-form"/);
  for (const field of ['name', 'email', 'organization', 'message']) {
    assert.match(page, new RegExp(`name="${field}"[^>]*required`), `missing required ${field}`);
  }
  assert.match(page, /id="demo-status"[^>]*aria-live="polite"/);
  assert.match(page, /mailto:bassam@bbrizly\.com/);
});

test('clinician page includes a reduced-motion path and avoids patient-data claims', () => {
  assert.match(page, /prefers-reduced-motion: reduce/);
  assert.match(page, /No patient data is stored on this page/i);
  assert.doesNotMatch(page, /real patient|patient records from your/i);
});

test('the switch is under the header, not inside the header that follows you', () => {
  for (const [name, text] of pages) {
    const header = text.slice(text.indexOf('<header'), text.indexOf('</header>'));
    assert.doesNotMatch(header, /data-audience-switch/, `${name}: switch is back in the sticky header`);
    assert.match(text.slice(text.indexOf('</header>')), /data-audience-switch/, `${name}: switch is missing below the header`);
  }
});

test('the hero carries the programs band, so it is not a scroll away', () => {
  const hero = page.slice(page.indexOf('hero-shell'), page.indexOf('<section id="workspace"'));
  assert.match(hero, /Craig Hospital/);
  assert.match(hero, /not customers or endorsements/i);
});

test('the page sells a free app and a paid workflow, never "open core"', () => {
  assert.doesNotMatch(page, /open core/i);
  assert.match(page, /Free app, paid workflow/);
});

test('the product is named QuadStick Clinic, never the old misspelling', () => {
  assert.doesNotMatch(page, /Quasic/i);
  assert.match(page, /QuadStick Clinic/);
});

test('the clinic page carries clinic-specific navigation', () => {
  const header = page.slice(page.indexOf('<header class="nav"'), page.indexOf('</header>'));
  assert.match(header, /QuadStick&nbsp;<span class="sub">Clinic<\/span>/);
  assert.match(header, /href="#workspace">Workspace/);
  assert.match(header, /href="#what">What changes/);
  assert.match(header, /href="#inside">Inside Clinic/);
  assert.match(header, /href="#principles">Privacy/);
  assert.match(header, /class="btn primary" href="#demo">\s*Book a demo/);
  assert.doesNotMatch(header, />\s*Download\s*</);
  assert.doesNotMatch(header, /index\.html#/);
  assert.match(page, /\.nav-links \.btn\.primary\{background:var\(--clinic\)/);
});

test('the switch sits inside the hero on both pages, so the gradient runs behind it', () => {
  for (const [name, text] of pages) {
    const shell = text.indexOf('hero-shell');
    assert.ok(shell > 0, `${name} has no hero shell`);
    assert.ok(text.indexOf('data-audience-switch') > shell, `${name}: the switch is above the hero gradient`);
  }
});

test('the screenshots are shown whole, not cropped', () => {
  assert.doesNotMatch(page, /\.shot img\{[^}]*object-fit:cover/);
  assert.match(page, /\.shot img\{[^}]*aspect-ratio:1440\/900[^}]*object-fit:contain/);
  assert.doesNotMatch(page, /screenshot-[a-z-]+\.png"[^>]*width="1600"/);
});

test('no named person is quoted until their own words and photo are in hand', () => {
  const live = page.replace(/<!--[\s\S]*?-->/g, '');
  assert.doesNotMatch(live, /Drew Redepenning/);
});

test('the phone preview is parked, markup and script together', () => {
  const live = page.replace(/<!--[\s\S]*?-->/g, '');
  assert.doesNotMatch(live, /<section id="phone">/, 'the phone section is live again');
  assert.doesNotMatch(live, /id="tab-edit"/);
  // the CSS stays, so bringing it back is uncommenting two blocks and nothing else
  assert.match(page, /\.phone-screen\{/);
  assert.match(page, /<!-- ===== PHONE, parked =====/);
});

test('both pages carry the same six marks, each a real file and a real link', () => {
  const links = [
    ['logos/craig-hospital.svg', 'https://craighospital.org/'],
    ['logos/shepherd-center.svg', 'https://shepherd.org/'],
    ['logos/live-life-therapy.webp', 'https://livelifetherapysolutions.com/'],
    ['logos/aztap.png', 'https://aztap.org/'],
    ['logos/ablegamers.webp', 'https://ablegamers.org/'],
    ['logos/respawn-foundation.webp', 'https://www.respawnfoundation.org/'],
  ];
  for (const [file, href] of links) {
    assert.ok(existsSync(new URL(`../docs/${file}`, import.meta.url)), `missing logo file: ${file}`);
    for (const [name, text] of pages) {
      assert.match(text, new RegExp(`href="${href}"`), `${name} does not link ${href}`);
      assert.match(text, new RegExp(`src="${file}"`), `${name} does not show ${file}`);
    }
  }
});

test('the marks slide on their own, and stop for anyone who asked motion to stop', () => {
  for (const [name, text] of pages) {
    assert.match(text, /\.track\{[^}]*animation:slide/, `${name}: the marks do not move`);
    assert.match(text, /\.rail:hover \.track[^{]*\{animation-play-state:paused\}/, `${name}: pointing at it does not stop it`);
    const reduced = text.slice(text.indexOf('prefers-reduced-motion'));
    assert.match(reduced.slice(0, 400), /\.track\{animation:none\}/, `${name}: reduced motion still slides`);
  }
});

test('picking a client cannot resize the workspace frame', () => {
  // every pane is a fixed track in a fixed height, so a longer history scrolls
  // its own pane instead of growing the window under the pointer
  assert.match(page, /\.dash-body\{[^}]*grid-template-columns:\d+px minmax\(0,1fr\) \d+px;height:clamp\(/);
  assert.doesNotMatch(page, /\.dash-body\{[^}]*min-height:clamp/);
  assert.match(page, /\.dash-side,\.dash-main,\.dash-rail\{[^}]*overflow-y:auto/);
});

test('the workspace is its own section, not a column of the hero', () => {
  assert.match(page, /<section id="workspace">/);
  assert.ok(page.indexOf('id="clinic-console"') > page.indexOf('<section id="workspace">'));
  assert.doesNotMatch(page, /hero-grid/);
});

test('the three shots sit side by side', () => {
  assert.match(page, /\.shots\{[^}]*grid-template-columns:repeat\(3,minmax\(0,1fr\)\)/);
  assert.doesNotMatch(page, /shot lead/);
});

test('the nav logo is the same mark on both pages', () => {
  // the section icons also use .glyph, and their ground was landing on the logo
  assert.match(page, /\.brand \.glyph\{[^}]*background:none/);
});

test('both heroes open on the same picture, in the same place', () => {
  for (const [name, text] of pages) {
    assert.match(text, /class="hero-stick" src="hero-device\.png"/, `${name} lost the hero art`);
  }
  const rule = text => text.slice(text.indexOf('.hero-stick{'), text.indexOf('}', text.indexOf('.hero-stick{')));
  assert.equal(rule(page), rule(homepage));
});

test('the testimonials are a file you append to, and the band hides while it is empty', () => {
  assert.match(homepage, /<script src="testimonials\.js"><\/script>/);
  assert.match(homepage, /id="quote-rail" hidden/);
  assert.match(homepage, /quoteRail\.hidden = false/);
  assert.match(quotes, /const TESTIMONIALS = \[/);
});

test('no user is quoted on the free page until they said it', () => {
  // the same rule as the clinician voices section: the mechanism ships, the
  // words wait for a real person
  const entries = quotes.split('\n').filter(line => /^\s*\{/.test(line));
  assert.deepEqual(entries, [], `testimonials.js has an uncommented entry: ${entries}`);
});

test('neither page uses an em dash or an en dash', () => {
  for (const [name, text] of pages) {
    const dash = text.match(/.{40}[–—].{40}/s);
    assert.equal(dash, null, `${name} has a dash: ${dash?.[0]}`);
  }
});
