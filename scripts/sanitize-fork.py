#!/usr/bin/env python3
"""One-way cleanup of the vendored engine fork (brief §2.2, §4): leaves out what the product does not ship (updater,
dashboard, upstream docs) and renames the upstream project throughout, files and contents, uniformly so the code keeps
working. Licence texts stay word for word; model file names on the download servers stay as they are.

    python scripts/sanitize-fork.py engine/tiered
"""
import re
import shutil
import sys
from pathlib import Path

root = Path(sys.argv[1]).resolve()
SKIP_DIRS = {"third_party/llama.cpp"}
REMOVE = ["docs", "bench", ".github", "serve/web", "third_party/wheels", "maya.sh", "setup.sh", "START-MAYA.bat", "chat.py",
          "CHANGELOG.md", "CONTRIBUTING.md", "README.md", "README.ja.md", "README.pt-BR.md", "README.zh-CN.md"]
TEXT = {".py", ".cu", ".cuh", ".cpp", ".h", ".hpp", ".c", ".cmake", ".txt", ".json", ".md", ".sh", ".bat", ".jinja", ".patch", ".in",
        ".hip", ".ps1", ".yml", ".yaml", ".toml", ""}
KEEP_NAME = re.compile(r"(?<=Flash-)Maya(?=[-_/])")   # GLM-5.3-Flash-Maya-S24-...gguf: the name on the download server
STUB = '''"""The updater is not part of this product: new versions come with the installer."""
RESTART_EXIT = 76


class Updater:
    def __init__(self, current=None, root=None):
        self.current = current

    def busy(self):
        return False

    def check(self, force=False):
        return {"current": self.current, "latest": None}

    def start(self, svc):
        raise ValueError("updates come with the installer")
'''


def rename_text(text: str) -> str:
    parts = KEEP_NAME.sub("\0", text)
    parts = parts.replace("MAYA", "WAM").replace("Maya", "Wam").replace("maya", "wam")
    return parts.replace("\0", "Maya")


for rel in REMOVE:
    p = root / rel
    if p.is_dir():
        shutil.rmtree(p)
    elif p.exists():
        p.unlink()
for p in root.glob("tools/test_*"):
    p.unlink()
(root / "serve" / "update.py").write_text(STUB, encoding="utf-8")
for name in ("test_update.py", "test_dashboard.py"):
    (root / "serve" / name).unlink(missing_ok=True)

server = root / "serve" / "server.py"
s = server.read_text(encoding="utf-8")
old = 'body = (ROOT / "serve" / "web" / "index.html").read_bytes()'
assert s.count(old) == 1
s = s.replace(old, 'body = b"<!doctype html><title>WinAIMagic</title>The local engine is running."')
server.write_text(s, encoding="utf-8")

for path in sorted(root.rglob("*"), key=lambda p: len(p.parts), reverse=True):
    rel = path.relative_to(root).as_posix()
    if any(rel == d or rel.startswith(d + "/") for d in SKIP_DIRS):
        continue
    if path.is_file() and path.suffix.lower() in TEXT and not path.name.upper().startswith("LICENSE"):
        raw = path.read_bytes()
        try:
            text = raw.decode("utf-8")
        except UnicodeDecodeError:
            text = None
        if text is not None:
            new = rename_text(text)
            if new != text:
                path.write_bytes(new.encode("utf-8"))
    if re.search("maya", path.name, re.I):
        path.rename(path.with_name(rename_text(path.name)))
print("sanitized", root)
