# Adaptive Profile Registry

This directory is the canonical, public data store for adaptive-control profiles.

The registry deliberately contains **structured control data only**. It is not a forum, file host, patient record system, or social network. That keeps the project cheap to operate, easy to validate, portable, and difficult to abuse.

## Model

- `games/` describes semantic game actions such as `attack`, `jump`, and `interact`, plus optional platform defaults.
- `devices/` describes adaptive-controller inputs such as `right-sip` or `switch-1`.
- `profiles/` maps game actions to device inputs. For QuadStick V1, the install source remains a public Google Sheet so QCM can reuse its existing importer.
- `index.json` is generated from those files and is the only catalog QCM/the website need to fetch.
- `fixtures/` contains non-public examples used to exercise the validator.

All IDs are stable slugs. UI labels may change; IDs should not.

## Why GitHub is the backend

Git provides durable history, attribution, diffs and rollback. GitHub supplies authentication, issue intake, Actions validation and public hosting without a custom database. A future API can mirror this repository without changing the file format.

## Publishing

Profiles are submitted through a constrained form. The form opens a structured GitHub issue containing a base64url payload. `.github/workflows/registry-submission.yml` runs `tools/registry/ingest-issue.mjs`, discards unknown fields, derives the profile ID itself, validates every reference, regenerates `index.json`, and commits only valid data.

There is no arbitrary file upload and no arbitrary description field.

## Local validation

```bash
node tools/registry/validate.mjs --check
```

To regenerate the catalog after editing data:

```bash
node tools/registry/validate.mjs --write-index
```

## Privacy boundary

Never put names, diagnoses, clinical notes, contact details, patient identifiers, or other health information in this repository. A future clinical product must use separate infrastructure and a separate data model.
