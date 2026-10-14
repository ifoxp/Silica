"""Writes src/Silica/Languages/uk.json from tools/uk_strings.py, keeping only keys that exist in en.json."""

import json
from pathlib import Path

from uk_strings import SYNONYMS, UK

LANGS = Path(__file__).resolve().parent.parent / "src" / "Silica" / "Languages"
en = json.loads((LANGS / "en.json").read_text(encoding="utf-8"))["strings"]

pack = {
    "language": {"code": "uk", "name": "Українська", "englishName": "Ukrainian", "author": "Silica", "plural": "east-slavic"},
    "strings": {k: UK[k] for k in en if k in UK},
    "synonyms": SYNONYMS,
}
(LANGS / "uk.json").write_text(json.dumps(pack, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

missing = [k for k in en if k not in UK]
print(f"uk.json: {len(pack['strings'])} of {len(en)} keys")
for k in missing:
    print("  missing:", k)
