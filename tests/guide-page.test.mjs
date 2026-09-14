import assert from 'node:assert/strict';
import { existsSync } from 'node:fs';
import { readFile } from 'node:fs/promises';
import test from 'node:test';

const SERVED = 'https://bassamkamal.dev/Quadstick-Config-Manager/how-to-make-a-quadstick-profile/';
const pageUrl = new URL('../docs/how-to-make-a-quadstick-profile/index.html', import.meta.url);
const page = await readFile(pageUrl, 'utf8');
const live = page.replace(/<!--[\s\S]*?-->/g, '');

const attr = (tag, name) => tag.match(new RegExp(`\\s${name}="([^"]*)"`))?.[1];
const meta = (key) => live.match(new RegExp(`<meta (?:name|property)="${key}" content="([^"]*)"`))?.[1];

test('the guide is one page with one h1 that answers the search', () => {
  assert.equal((live.match(/<h1[\s>]/g) ?? []).length, 1);
  assert.match(live, /<title>[^<]*How to make a QuadStick game profile[^<]*<\/title>/);
});

test('canonical and og:url are the absolute served URL', () => {
  assert.equal(live.match(/<link rel="canonical" href="([^"]*)"/)?.[1], SERVED);
  assert.equal(meta('og:url'), SERVED);
});

test('the meta description is there and short enough to show whole', () => {
  const description = meta('description');
  assert.ok(description, 'no meta description');
  assert.ok(description.length <= 155, `description is ${description.length} characters`);
});

test('the social preview points at a real screenshot by absolute URL', () => {
  const image = meta('og:image');
  assert.match(image, /^https:\/\/bassamkamal\.dev\/Quadstick-Config-Manager\/[a-z-]+\.png$/);
  assert.ok(existsSync(new URL(`../docs/${image.split('/').pop()}`, import.meta.url)), `${image} is not in docs/`);
  assert.equal(meta('twitter:card'), 'summary_large_image');
  assert.ok(meta('og:title') && meta('og:description'));
});

test('every image resolves to a file and says what it shows', () => {
  const images = live.match(/<img\b[^>]*>/g) ?? [];
  assert.ok(images.length >= 5, 'the guide lost its screenshots');
  for (const img of images) {
    const src = attr(img, 'src');
    assert.ok(existsSync(new URL(src, pageUrl)), `missing image: ${src}`);
    assert.notEqual(attr(img, 'alt'), undefined, `${src} has no alt`);
    assert.match(attr(img, 'width') ?? '', /^\d+$/, `${src} has no width`);
    assert.match(attr(img, 'height') ?? '', /^\d+$/, `${src} has no height`);
  }
  // the logo is decoration next to its own name; every screenshot needs real words
  for (const img of images.filter(i => /screenshot-/.test(i))) {
    assert.ok((attr(img, 'alt') ?? '').length > 40, `${attr(img, 'src')} has a thin alt`);
  }
  for (const source of live.match(/<source\b[^>]*>/g) ?? []) {
    const srcset = attr(source, 'srcset');
    assert.ok(existsSync(new URL(srcset, pageUrl)), `missing dark screenshot: ${srcset}`);
  }
});

test('no em dash or en dash anywhere in the file', () => {
  const dash = page.match(/.{0,40}[\u2013\u2014].{0,40}/s);
  assert.equal(dash, null, `dash found: ${dash?.[0]}`);
});

test('the switched-off agent feature is never mentioned', () => {
  for (const phrase of [/Set up a game/i, /Ask for a change/i, /\bagent\b/i, /\bAI\b/]) {
    assert.doesNotMatch(live, phrase);
  }
});

test('the structured data is one TechArticle that parses', () => {
  const blocks = [...live.matchAll(/<script type="application\/ld\+json">([\s\S]*?)<\/script>/g)];
  assert.equal(blocks.length, 1);
  const data = JSON.parse(blocks[0][1]);
  assert.equal(data['@type'], 'TechArticle');
  assert.equal(data.headline, 'How to make a QuadStick game profile');
  assert.equal(data.datePublished, '2026-09-13');
  assert.equal(data.author?.name, 'Bassam Kamal');
  assert.equal(data.author?.url, 'https://bassamkamal.dev/');
  assert.equal(data.publisher?.name, 'Bassam Kamal');
  assert.match(data.image, /^https:\/\//);
  assert.equal(data.about?.name, 'QuadStick');
});

test('the guide links home and to the downloads', () => {
  assert.match(live, /href="\.\.\/"/);
  assert.match(live, /href="\.\.\/#download"/);
});
