#!/usr/bin/env python3
"""Generate React catalogs from the shipping QuadStick.App RESX resources."""
from __future__ import annotations

import hashlib
import json
import re
from pathlib import Path
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
LEGACY = ROOT / "src" / "QuadStick.App"
OUT = ROOT / "src" / "i18n" / "catalogs"
OVERLAY = ROOT / "src" / "i18n" / "rewrite-strings.json"
LOCALES = ("ar", "de", "es", "fr", "hi", "it", "ja", "ko", "nl", "pl", "pt", "zh-Hans")
INVARIANT = {
    "Rewrite_ProductName": "QuadStick Config Manager",
    "Rewrite_PseudoLocaleName": "Pseudo (finds missed text)",
}
PLACEHOLDER = re.compile(r"\{\d+[^}]*\}")
ACCENT = str.maketrans("aeiouAEIOUcnsyzCNSYZ", "áéíóúÁÉÍÓÚçñšýžÇÑŠÝŽ")


def read_resx(path: Path) -> dict[str, str]:
    root = ET.parse(path).getroot()
    out: dict[str, str] = {}
    for node in root.findall("data"):
        key = node.get("name")
        value = node.find("value")
        if key and value is not None:
            out[key] = value.text or ""
    return out


def holes(value: str) -> list[str]:
    return sorted(set(PLACEHOLDER.findall(value)))


def pseudo(value: str) -> str:
    parts = re.split(r"(\{\d+[^}]*\})", value)
    body = "".join(piece if index % 2 else piece.translate(ACCENT) for index, piece in enumerate(parts))
    return f"[{body} {'x' * max(1, int(len(value) * 0.4))}]"


def write_json(path: Path, value: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2, sort_keys=True) + "\n", encoding="utf-8")


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main() -> None:
    english_path = LEGACY / "Strings.resx"
    english = read_resx(english_path)
    overlay = json.loads(OVERLAY.read_text(encoding="utf-8"))
    rewrite_keys = sorted(set(overlay["en"]) | set(INVARIANT))
    if any(key in english for key in rewrite_keys):
        raise SystemExit("rewrite-only localization key collides with legacy RESX")

    english_rewrite = {**overlay["en"], **INVARIANT}
    catalogs: dict[str, dict[str, str]] = {"en": {**english, **english_rewrite}}
    source_hashes = {"en": sha256(english_path)}
    translated_rewrite_keys = set(overlay["en"])
    for locale in LOCALES:
        path = LEGACY / f"Strings.{locale}.resx"
        translated = read_resx(path)
        unknown = sorted(set(translated) - set(english))
        if unknown:
            raise SystemExit(f"{path}: unknown keys: {unknown[:10]}")
        for key, translated_value in translated.items():
            if holes(translated_value) != holes(english[key]):
                raise SystemExit(f"{path}: placeholders differ for {key}")
        rewrite = overlay.get(locale, {})
        missing = sorted(translated_rewrite_keys - set(rewrite))
        if missing:
            raise SystemExit(f"{OVERLAY}: {locale} missing rewrite keys: {missing}")
        catalogs[locale] = {**english, **translated, **rewrite, **INVARIANT}
        source_hashes[locale] = sha256(path)

    catalogs["qps-ploc"] = {key: pseudo(value) for key, value in catalogs["en"].items()}
    for locale, catalog in catalogs.items():
        write_json(OUT / f"{locale}.json", catalog)
    write_json(OUT / "baseline-keys.json", sorted(english))
    write_json(OUT / "rewrite-keys.json", rewrite_keys)
    write_json(OUT / "catalog-meta.json", {
        "baselineKeyCount": len(english),
        "rewriteKeyCount": len(rewrite_keys),
        "locales": ["en", *LOCALES],
        "sourceSha256": source_hashes,
    })
    print(f"generated {len(catalogs)} catalogs; {len(english)} legacy keys + {len(rewrite_keys)} rewrite keys")


if __name__ == "__main__":
    main()
