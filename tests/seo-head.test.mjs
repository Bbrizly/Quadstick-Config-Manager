import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const SITE = 'https://bassamkamal.dev/Quadstick-Config-Manager/';
const GUIDE = `${SITE}how-to-make-a-quadstick-profile/`;
const docs = file => new URL(`../docs/${file}`, import.meta.url);

// every public page, and the one address it should be filed under
const ADDRESS = {
  'index.html': SITE,
  'clinic.html': `${SITE}clinic.html`,
  'how-it-works.html': `${SITE}how-it-works.html`,
  'privacy.html': `${SITE}privacy.html`,
  'ios-privacy.html': `${SITE}ios-privacy.html`,
};

// comments hold parked sections, and a parked image is not on the page
const pages = await Promise.all(Object.keys(ADDRESS).map(async name =>
  [name, (await readFile(docs(name), 'utf8')).replace(/<!--[\s\S]*?-->/g, '')]));
const homepage = pages.find(([name]) => name === 'index.html')[1];
const head = text => text.slice(0, text.indexOf('</head>'));
const meta = (text, key) =>
  head(text).match(new RegExp(`<meta (?:property|name)="${key}" content="([^"]*)"`))?.[1];
// a site URL is a file in docs/, and a folder URL is its index.html
const fileFor = url => {
  const path = decodeURI(url.slice(SITE.length));
  return path === '' || path.endsWith('/') ? `${path}index.html` : path;
};

const luminance = hex => {
  const [r, g, b] = [0, 2, 4].map(i => parseInt(hex.slice(i + 1, i + 3), 16) / 255)
    .map(c => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};

test('every page names its own canonical address', () => {
  for (const [name, text] of pages) {
    const canonical = head(text).match(/<link rel="canonical" href="([^"]*)"/)?.[1];
    assert.equal(canonical, ADDRESS[name], `${name} has the wrong canonical`);
  }
});

test('every page has a link preview a scraper can fetch', () => {
  for (const [name, text] of pages) {
    for (const key of ['og:site_name', 'og:title', 'og:description', 'og:image:alt']) {
      assert.ok(meta(text, key), `${name} has no ${key}`);
    }
    assert.equal(meta(text, 'og:url'), ADDRESS[name], `${name}: og:url is not the canonical`);
    const image = meta(text, 'og:image') ?? '';
    // a relative og:image is resolved by nobody, so the share shows no picture
    assert.ok(image.startsWith(SITE), `${name}: og:image is not an absolute site URL: ${image}`);
    assert.ok(existsSync(docs(fileFor(image))), `${name}: og:image ${image} is not in docs/`);
    assert.match(meta(text, 'og:image:width') ?? '', /^\d+$/, `${name} has no og:image:width`);
    assert.match(meta(text, 'og:image:height') ?? '', /^\d+$/, `${name} has no og:image:height`);
    assert.equal(meta(text, 'twitter:card'), 'summary_large_image', `${name} has no large twitter card`);
  }
});

test('every page has a meta description', () => {
  for (const [name, text] of pages) {
    assert.ok((meta(text, 'description') ?? '').length >= 50, `${name} has no real meta description`);
  }
});

test('every image says its size, and every file it names is there', () => {
  for (const [name, text] of pages) {
    for (const img of text.match(/<img\b[^>]*>/g) ?? []) {
      assert.match(img, /\swidth="\d+"/, `${name}: no width on ${img}`);
      assert.match(img, /\sheight="\d+"/, `${name}: no height on ${img}`);
      const src = img.match(/\ssrc="([^"]*)"/)?.[1] ?? '';
      const set = (img.match(/\ssrcset="([^"]*)"/)?.[1] ?? '').split(',').map(s => s.trim().split(/\s+/)[0]);
      for (const file of [src, ...set].filter(f => f && !/^(https?:|data:)/.test(f))) {
        assert.ok(existsSync(docs(file)), `${name}: ${file} is missing`);
      }
    }
  }
});

test('the hero image loads first, and the screenshots wait', () => {
  const hero = homepage.match(/<img class="hero-stick"[^>]*>/)?.[0] ?? '';
  assert.match(hero, /fetchpriority="high"/);
  assert.doesNotMatch(hero, /loading="lazy"/);
  const shots = homepage.match(/<img [^>]*src="screenshot-[^>]*>/g) ?? [];
  assert.ok(shots.length >= 5, 'the feature screenshots are gone');
  for (const shot of shots) assert.match(shot, /loading="lazy"/, `not lazy: ${shot}`);
});

test('the nav logo is not the 1000px source file', async () => {
  const demo = await readFile(docs('clinic-demo.js'), 'utf8');
  for (const [name, text] of [...pages, ['clinic-demo.js', demo]]) {
    assert.ok(!/src="QSLogo\.png"/.test(text), `${name} still loads the full size logo`);
  }
});

test('the home page describes the app as structured data', () => {
  const block = homepage.match(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/)?.[1];
  assert.ok(block, 'index.html has no JSON-LD');
  const app = JSON.parse(block);
  assert.equal(app['@type'], 'SoftwareApplication');
  assert.equal(app.name, 'QuadStick Config Manager');
  for (const os of ['Windows', 'macOS', 'Linux']) assert.match(app.operatingSystem, new RegExp(os));
  assert.equal(app.offers?.price, '0');
  assert.equal(app.isAccessibleForFree, true);
  assert.equal(app.author?.name, 'Bassam Kamal');
  // every link it claims is one the page itself already makes
  const links = new Set([...homepage.matchAll(/href="([^"]*)"/g)].map(m => m[1].replaceAll('&amp;', '&')));
  for (const url of [app.downloadUrl, ...app.sameAs]) assert.ok(links.has(url), `not linked on the page: ${url}`);
  assert.ok(app.screenshot.startsWith(SITE) && existsSync(docs(fileFor(app.screenshot))), 'screenshot is not a real site file');
  // nothing that goes stale or cannot be checked
  for (const key of ['softwareVersion', 'aggregateRating', 'review']) assert.ok(!(key in app), `has ${key}`);
});

test('the sitemap lists every public page and the profile guide', async () => {
  const xml = await readFile(docs('sitemap.xml'), 'utf8');
  assert.match(xml, /^<\?xml version="1\.0" encoding="UTF-8"\?>\s*<urlset xmlns="http:\/\/www\.sitemaps\.org\/schemas\/sitemap\/0\.9">/);
  assert.match(xml, /<\/urlset>\s*$/);
  assert.equal((xml.match(/<url>/g) ?? []).length, (xml.match(/<\/url>/g) ?? []).length, 'unbalanced <url>');
  const listed = [...xml.matchAll(/<loc>([^<]*)<\/loc>/g)].map(m => m[1]);
  assert.deepEqual([...listed].toSorted(), [...Object.values(ADDRESS), GUIDE].toSorted());
  for (const url of listed) {
    // the guide is built in its own branch, so it may not be on disk yet
    if (url === GUIDE && !existsSync(docs(fileFor(url)))) continue;
    assert.ok(existsSync(docs(fileFor(url))), `sitemap lists a page that is not there: ${url}`);
  }
});

test('the home page footer links the code tour and the profile guide', () => {
  const footer = homepage.slice(homepage.lastIndexOf('<footer'));
  assert.match(footer, /<a href="how-it-works\.html">/);
  assert.match(footer, /<a href="how-to-make-a-quadstick-profile\/">How to make a profile<\/a>/);
});

test('faint text is readable on both papers', () => {
  const ratio = (a, b) => {
    const [hi, lo] = [luminance(a), luminance(b)].toSorted((x, y) => y - x);
    return (hi + 0.05) / (lo + 0.05);
  };
  for (const [name, text] of pages.filter(([, t]) => /--ink-faint:/.test(t))) {
    const token = key => text.match(new RegExp(`--${key}:\\s*(#[0-9A-Fa-f]{6})`))?.[1];
    for (const ground of ['paper', 'paper-2']) {
      const r = ratio(token('ink-faint'), token(ground));
      assert.ok(r >= 4.5, `${name}: faint ink on ${ground} is ${r.toFixed(2)}:1`);
    }
  }
});

test('the fonts come from this site, not a render-blocking request to Google', () => {
  for (const [name, text] of pages) {
    assert.doesNotMatch(text, /fonts\.(googleapis|gstatic)\.com/, `${name} still loads Google Fonts`);
    for (const [, file] of head(text).matchAll(/(?:url\(|href=")(fonts\/[^)"]+)/g)) {
      assert.ok(existsSync(docs(file)), `${name}: ${file} is missing`);
    }
  }
});
