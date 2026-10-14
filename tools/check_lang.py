"""Checks language files against en.json.

    python tools/check_lang.py [code ...]

Reports invalid JSON, missing or unknown keys, broken {0} placeholders and plural forms that do not
match the language's plural rule. Exit code 1 when a file has errors.
"""

import json
import re
import sys
from pathlib import Path

LANGS = Path(__file__).resolve().parent.parent / "src" / "Silica" / "Languages"

# Number of forms each plural rule expects (see PluralRules.cs).
PLURAL_FORMS = {
    "none": 1, "one-other": 2, "zero-one-other": 2, "east-slavic": 3, "polish": 3, "czech": 3,
    "lithuanian": 3, "latvian": 3, "romanian": 3, "slovenian": 4, "arabic": 6, "hebrew": 3,
}
PLURAL_KEYS = {"{0} sensors", "{0} results", "{0} threads", "{0} times", "{0} cores"}
PLACEHOLDER = re.compile(r"\{\d+\}")


def check(path: Path, en: dict) -> list[str]:
    errors = []
    try:
        data = json.loads(path.read_text(encoding="utf-8-sig"))
    except json.JSONDecodeError as e:
        return [f"invalid JSON: {e}"]
    meta = data.get("language", {})
    for field in ("code", "name", "englishName", "plural"):
        if not meta.get(field):
            errors.append(f"language.{field} is missing")
    rule = meta.get("plural", "one-other")
    if rule not in PLURAL_FORMS:
        errors.append(f"unknown plural rule '{rule}'")
    strings = data.get("strings", {})
    missing = [k for k in en if k not in strings]
    unknown = [k for k in strings if k not in en]
    if missing:
        errors.append(f"{len(missing)} missing keys, e.g. {missing[:3]}")
    if unknown:
        errors.append(f"{len(unknown)} unknown keys, e.g. {unknown[:3]}")
    for key, value in strings.items():
        if key not in en or not isinstance(value, str):
            continue
        forms = value.split("|")
        if key in PLURAL_KEYS:
            if rule in PLURAL_FORMS and len(forms) != PLURAL_FORMS[rule]:
                errors.append(f"'{key}' has {len(forms)} plural forms, '{rule}' needs {PLURAL_FORMS[rule]}")
        elif "|" in value:
            errors.append(f"'{key}' contains '|' but is not a plural key")
        expected = sorted(set(PLACEHOLDER.findall(key)))
        for form in forms:
            if sorted(set(PLACEHOLDER.findall(form))) != expected:
                errors.append(f"placeholders differ in '{key}': '{form}'")
                break
    return errors


def main() -> int:
    en = json.loads((LANGS / "en.json").read_text(encoding="utf-8"))["strings"]
    codes = sys.argv[1:]
    files = [LANGS / f"{c}.json" for c in codes] if codes else sorted(p for p in LANGS.glob("*.json") if p.name != "en.json")
    failed = False
    for f in files:
        errors = check(f, en) if f.exists() else ["file not found"]
        print(f"{f.name}: {'OK' if not errors else str(len(errors)) + ' problem(s)'}")
        for e in errors[:15]:
            print("   ", e)
        failed |= bool(errors)
    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
