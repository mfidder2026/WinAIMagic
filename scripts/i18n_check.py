#!/usr/bin/env python3
"""Lists every L("...") / L($"...") key in src and what en/de/fr lack, plus Mac wording left in keys or translations.

    python scripts/i18n_check.py [--json out.json]
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
LIT = re.compile(r'\bL\((\$?)"((?:[^"\\]|\\.)*)"')
MAC = re.compile(r"\b(Mac|macOS|Finder|Thunderbolt|GPU-limiet|Spotlight|Terminal\.app|Apple|Metal)\b")


def key_of(interp: str, body: str) -> str:
    text = body.replace('\\"', '"').replace("\\n", "\n").replace("\\t", "\t").replace("\\\\", "\\")
    if not interp:
        return text
    out, depth = "", 0
    i = 0
    while i < len(text):
        c = text[i]
        if c == "{" and text[i:i + 2] == "{{":
            out += "{"
            i += 2
            continue
        if c == "}" and text[i:i + 2] == "}}":
            out += "}"
            i += 2
            continue
        if c == "{":
            depth += 1
            if depth == 1:
                out += "%@"
        elif c == "}":
            depth -= 1
        elif depth == 0:
            out += "%%" if c == "%" else c
        i += 1
    return out


keys = {}
for path in (ROOT / "src").rglob("*.cs"):
    if "\\obj\\" in str(path) or "\\bin\\" in str(path):
        continue
    for m in LIT.finditer(path.read_text(encoding="utf-8")):
        keys.setdefault(key_of(m.group(1), m.group(2)), path.name)

tables = {c: json.loads((ROOT / f"src/WinAIMagic.Core/i18n/{c}.json").read_text(encoding="utf-8")) for c in ("en", "de", "fr")}
missing = {c: sorted(k for k in keys if k not in t) for c, t in tables.items()}
unused = sorted(k for k in tables["en"] if k not in keys)
mac_keys = sorted(k for k in keys if MAC.search(k))
mac_values = {c: sorted(k for k, v in t.items() if k in keys and MAC.search(v)) for c, t in tables.items()}
print(f"keys in code: {len(keys)}; table sizes: " + ", ".join(f"{c} {len(t)}" for c, t in tables.items()))
for c in tables:
    print(f"{c}: {len(missing[c])} missing, {len(mac_values[c])} translations with Mac wording")
print(f"keys with Mac wording: {len(mac_keys)}; table keys no longer used: {len(unused)}")
for k in mac_keys[:40]:
    print("  MAC KEY:", repr(k)[:130], keys[k])
if "--json" in sys.argv:
    out = {"missing": sorted({k for c in missing.values() for k in c}), "mac_keys": mac_keys,
           "mac_values": sorted({k for c in mac_values.values() for k in c}), "unused": unused}
    Path(sys.argv[sys.argv.index("--json") + 1]).write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
