#!/usr/bin/env node
import fs from 'node:fs';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';

const here = path.dirname(fileURLToPath(import.meta.url));
const root = path.resolve(here, '..', '..');
const registry = path.join(root, 'registry');
const slug = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
const profileSlug = /^[a-z0-9]+(?:-+[a-z0-9]+)*$/;
const githubLogin = /^[A-Za-z0-9-]{1,39}$/;
const sheetId = /^[A-Za-z0-9_-]{20,200}$/;
const platforms = new Set(['pc', 'xbox', 'playstation', 'switch']);
const variants = new Set(['standard', 'beginner', 'low-fatigue', 'competitive', 'one-mode', 'custom']);
const tags = new Set(['beginner', 'low-fatigue', 'competitive', 'one-mode', 'limited-sip', 'limited-puff']);
const behaviors = new Set(['normal', 'tap', 'hold', 'toggle', 'repeat']);
const categories = new Set(['movement', 'camera', 'combat', 'interaction', 'navigation', 'communication', 'system', 'other']);
const inputKinds = new Set(['digital', 'analog-2d', 'analog-1d', 'pointer', 'switch', 'sensor']);

function die(message) { throw new Error(message); }
function object(value, where) { if (!value || typeof value !== 'object' || Array.isArray(value)) die(`${where}: expected object`); return value; }
function exactKeys(value, allowed, where) { for (const key of Object.keys(value)) if (!allowed.has(key)) die(`${where}: unknown field ${key}`); }
function string(value, where, max = 120) { if (typeof value !== 'string' || value.length === 0 || value.length > max) die(`${where}: invalid string`); return value; }
function id(value, where) { string(value, where, 220); if (!slug.test(value)) die(`${where}: invalid slug`); return value; }
function profileId(value, where) { string(value, where, 220); if (!profileSlug.test(value)) die(`${where}: invalid profile id`); return value; }
function unique(values, where) { if (new Set(values).size !== values.length) die(`${where}: duplicates are not allowed`); }
function readJson(file) { try { return JSON.parse(fs.readFileSync(file, 'utf8')); } catch (e) { die(`${path.relative(root, file)}: ${e.message}`); } }
function jsonFiles(dir) { if (!fs.existsSync(dir)) return []; return fs.readdirSync(dir).filter(x => x.endsWith('.json')).sort().map(x => path.join(dir, x)); }
function assertFileId(file, value) { const base = path.basename(file, '.json'); if (base !== value.id) die(`${path.relative(root, file)}: file name must equal id (${value.id})`); }

export function validateGame(value, where = 'game') {
  object(value, where); exactKeys(value, new Set(['schemaVersion', 'id', 'name', 'status', 'platforms', 'actions']), where);
  if (value.schemaVersion !== 1) die(`${where}: schemaVersion must be 1`);
  id(value.id, `${where}.id`); string(value.name, `${where}.name`);
  if (!['published', 'draft'].includes(value.status)) die(`${where}.status: invalid status`);
  if (!Array.isArray(value.platforms) || value.platforms.length === 0) die(`${where}.platforms: expected non-empty array`);
  unique(value.platforms, `${where}.platforms`); for (const p of value.platforms) if (!platforms.has(p)) die(`${where}.platforms: unsupported ${p}`);
  if (!Array.isArray(value.actions) || value.actions.length === 0) die(`${where}.actions: expected non-empty array`);
  const actionIds = [];
  for (const [i, action] of value.actions.entries()) {
    const at = `${where}.actions[${i}]`; object(action, at); exactKeys(action, new Set(['id', 'label', 'category', 'defaultOutputs']), at);
    id(action.id, `${at}.id`); actionIds.push(action.id); string(action.label, `${at}.label`, 80);
    if (!categories.has(action.category)) die(`${at}.category: invalid category`);
    if (action.defaultOutputs !== undefined) {
      object(action.defaultOutputs, `${at}.defaultOutputs`); exactKeys(action.defaultOutputs, platforms, `${at}.defaultOutputs`);
      for (const [platform, outputs] of Object.entries(action.defaultOutputs)) {
        if (!value.platforms.includes(platform)) die(`${at}.defaultOutputs.${platform}: game does not list this platform`);
        if (!Array.isArray(outputs) || outputs.length === 0 || outputs.length > 4) die(`${at}.defaultOutputs.${platform}: expected 1-4 outputs`);
        unique(outputs, `${at}.defaultOutputs.${platform}`);
        for (const output of outputs) { string(output, `${at}.defaultOutputs.${platform}`, 60); if (!/^[a-z0-9]+(?:[-_][a-z0-9]+)*$/.test(output)) die(`${at}.defaultOutputs.${platform}: invalid output token`); }
      }
    }
  }
  unique(actionIds, `${where}.actions`); return value;
}

export function validateDevice(value, where = 'device') {
  object(value, where); exactKeys(value, new Set(['schemaVersion', 'id', 'name', 'manufacturer', 'status', 'inputs']), where);
  if (value.schemaVersion !== 1) die(`${where}: schemaVersion must be 1`);
  id(value.id, `${where}.id`); string(value.name, `${where}.name`); string(value.manufacturer, `${where}.manufacturer`);
  if (!['published', 'draft'].includes(value.status)) die(`${where}.status: invalid status`);
  if (!Array.isArray(value.inputs) || value.inputs.length === 0) die(`${where}.inputs: expected non-empty array`);
  const ids = [];
  for (const [i, input] of value.inputs.entries()) { const at = `${where}.inputs[${i}]`; object(input, at); exactKeys(input, new Set(['id', 'label', 'kind']), at); id(input.id, `${at}.id`); ids.push(input.id); string(input.label, `${at}.label`, 80); if (!inputKinds.has(input.kind)) die(`${at}.kind: invalid input kind`); }
  unique(ids, `${where}.inputs`); return value;
}

export function validateProfile(value, games, devices, where = 'profile') {
  object(value, where); exactKeys(value, new Set(['schemaVersion', 'id', 'gameId', 'platform', 'deviceId', 'variant', 'contributor', 'source', 'bindings', 'tags']), where);
  if (value.schemaVersion !== 1) die(`${where}: schemaVersion must be 1`);
  profileId(value.id, `${where}.id`); id(value.gameId, `${where}.gameId`); id(value.deviceId, `${where}.deviceId`);
  if (!platforms.has(value.platform)) die(`${where}.platform: unsupported platform`);
  if (!variants.has(value.variant)) die(`${where}.variant: unsupported variant`);
  if (!githubLogin.test(value.contributor ?? '')) die(`${where}.contributor: invalid GitHub login`);
  const expectedId = `${value.gameId}--${value.platform}--${value.deviceId}--${value.variant}--${value.contributor.toLowerCase()}`;
  if (value.id !== expectedId) die(`${where}.id: expected ${expectedId}`);
  const game = games.get(value.gameId); if (!game) die(`${where}.gameId: unknown game ${value.gameId}`);
  const device = devices.get(value.deviceId); if (!device) die(`${where}.deviceId: unknown device ${value.deviceId}`);
  if (!game.platforms.includes(value.platform)) die(`${where}.platform: ${game.name} does not list ${value.platform}`);
  object(value.source, `${where}.source`); exactKeys(value.source, new Set(['kind', 'sheetId', 'csvName']), `${where}.source`);
  if (value.source.kind !== 'google-sheet') die(`${where}.source.kind: only google-sheet is supported in V1`);
  if (!sheetId.test(value.source.sheetId ?? '')) die(`${where}.source.sheetId: invalid Google Sheet id`);
  if (value.source.csvName !== undefined && !/^[A-Za-z0-9][A-Za-z0-9._ -]{0,99}\.csv$/.test(value.source.csvName)) die(`${where}.source.csvName: invalid CSV file name`);
  if (!Array.isArray(value.bindings) || value.bindings.length === 0 || value.bindings.length > 200) die(`${where}.bindings: expected 1-200 bindings`);
  const actionSet = new Set(game.actions.map(x => x.id)), inputSet = new Set(device.inputs.map(x => x.id)), boundActions = [];
  for (const [i, binding] of value.bindings.entries()) {
    const at = `${where}.bindings[${i}]`; object(binding, at); exactKeys(binding, new Set(['actionId', 'inputId', 'behavior']), at);
    id(binding.actionId, `${at}.actionId`); id(binding.inputId, `${at}.inputId`);
    if (!actionSet.has(binding.actionId)) die(`${at}.actionId: ${binding.actionId} is not an action in ${game.id}`);
    if (!inputSet.has(binding.inputId)) die(`${at}.inputId: ${binding.inputId} is not an input on ${device.id}`);
    if (binding.behavior !== undefined && !behaviors.has(binding.behavior)) die(`${at}.behavior: invalid behavior`);
    boundActions.push(binding.actionId);
  }
  unique(boundActions, `${where}.bindings actionId`);
  if (!Array.isArray(value.tags) || value.tags.length > 6) die(`${where}.tags: expected array of at most 6 tags`);
  unique(value.tags, `${where}.tags`); for (const tag of value.tags) if (!tags.has(tag)) die(`${where}.tags: unsupported ${tag}`);
  return value;
}

function loadSet(dir, validator) { const map = new Map(); for (const file of jsonFiles(dir)) { const value = validator(readJson(file), path.relative(root, file)); assertFileId(file, value); if (map.has(value.id)) die(`${path.relative(root, file)}: duplicate id ${value.id}`); map.set(value.id, value); } return map; }
function stable(value) { return JSON.stringify(value, null, 2) + '\n'; }
function buildIndex(games, devices, profiles) { return { schemaVersion: 1, games: [...games.values()].filter(x => x.status === 'published').sort((a, b) => a.id.localeCompare(b.id)), devices: [...devices.values()].filter(x => x.status === 'published').sort((a, b) => a.id.localeCompare(b.id)), profiles: [...profiles.values()].sort((a, b) => a.id.localeCompare(b.id)) }; }
function main() {
  const mode = process.argv[2] ?? '--check'; if (!['--check', '--write-index'].includes(mode)) die('usage: validate.mjs [--check|--write-index]');
  const games = loadSet(path.join(registry, 'games'), validateGame), devices = loadSet(path.join(registry, 'devices'), validateDevice), profiles = loadSet(path.join(registry, 'profiles'), value => validateProfile(value, games, devices));
  const fixtureGames = loadSet(path.join(registry, 'fixtures', 'games'), validateGame); loadSet(path.join(registry, 'fixtures', 'profiles'), value => validateProfile(value, new Map([...games, ...fixtureGames]), devices));
  const expected = stable(buildIndex(games, devices, profiles)), indexFile = path.join(registry, 'index.json');
  if (mode === '--write-index') fs.writeFileSync(indexFile, expected); else if ((fs.existsSync(indexFile) ? fs.readFileSync(indexFile, 'utf8') : '') !== expected) die('registry/index.json is stale; run node tools/registry/validate.mjs --write-index');
  console.log(`registry valid: ${games.size} game(s), ${devices.size} device(s), ${profiles.size} profile(s)`);
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) { try { main(); } catch (e) { console.error(e.message); process.exit(1); } }
