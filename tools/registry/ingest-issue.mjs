#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { validateDevice, validateGame, validateProfile } from './validate.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const registry = path.join(root, 'registry');
const loginRe = /^[A-Za-z0-9-]{1,39}$/;
const slugPart = value => String(value ?? '').toLowerCase().replace(/[^a-z0-9-]/g, '').replace(/-+/g, '-').replace(/^-|-$/g, '');

function die(message) { throw new Error(message); }
function files(dir) { return fs.readdirSync(dir).filter(x => x.endsWith('.json')).sort(); }
function loadMap(dir, validator) {
  const map = new Map();
  for (const name of files(dir)) { const value = JSON.parse(fs.readFileSync(path.join(dir, name), 'utf8')); validator(value, name); map.set(value.id, value); }
  return map;
}
function decodePayload(body) {
  const match = body.match(/<!--\s*PROFILE_PAYLOAD\s*\n([A-Za-z0-9_-]+)\nPROFILE_PAYLOAD\s*-->/);
  if (!match) die('No structured PROFILE_PAYLOAD marker was found. Submit from the registry form instead of editing the issue body.');
  let json;
  try { json = Buffer.from(match[1], 'base64url').toString('utf8'); } catch { die('Profile payload is not valid base64url.'); }
  try { return JSON.parse(json); } catch { die('Profile payload is not valid JSON.'); }
}

const body = process.env.ISSUE_BODY ?? '';
const actor = process.env.SUBMITTER ?? '';
if (!loginRe.test(actor)) die('Invalid GitHub submitter.');
const raw = decodePayload(body);
if (!raw || typeof raw !== 'object' || Array.isArray(raw)) die('Profile payload must be an object.');

const gameId = slugPart(raw.gameId);
const platform = slugPart(raw.platform);
const deviceId = slugPart(raw.deviceId);
const variant = slugPart(raw.variant);
const contributorSlug = actor.toLowerCase();
if (!gameId || !platform || !deviceId || !variant) die('Profile identity fields are missing.');

// Whitelist every stored field. Nothing else from an issue can enter the repository.
const profile = {
  schemaVersion: 1,
  id: `${gameId}--${platform}--${deviceId}--${variant}--${contributorSlug}`,
  gameId,
  platform,
  deviceId,
  variant,
  contributor: actor,
  source: {
    kind: 'google-sheet',
    sheetId: String(raw.source?.sheetId ?? ''),
    ...(raw.source?.csvName ? { csvName: String(raw.source.csvName) } : {})
  },
  bindings: Array.isArray(raw.bindings) ? raw.bindings.map(x => ({
    actionId: slugPart(x?.actionId),
    inputId: slugPart(x?.inputId),
    ...(x?.behavior && x.behavior !== 'normal' ? { behavior: slugPart(x.behavior) } : {})
  })) : [],
  tags: Array.isArray(raw.tags) ? raw.tags.map(slugPart) : []
};

const games = loadMap(path.join(registry, 'games'), validateGame);
const devices = loadMap(path.join(registry, 'devices'), validateDevice);
validateProfile(profile, games, devices, 'submission');

const outDir = path.join(registry, 'profiles');
fs.mkdirSync(outDir, { recursive: true });
const out = path.join(outDir, `${profile.id}.json`);
fs.writeFileSync(out, JSON.stringify(profile, null, 2) + '\n');
console.log(path.relative(root, out));
