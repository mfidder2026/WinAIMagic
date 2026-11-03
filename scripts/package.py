#!/usr/bin/env python3
"""Lays out dist\\app (the published app with its engine folder) and builds the installer.

    python scripts/package.py [--skip-publish] [--skip-setup]

Layout: dist\\app\\WinAIMagic.exe, dist\\app\\engine\\{tiered\\(serve, tools, bin), python\\, cuda\\, vulkan\\, cpu\\}.
Everything the user needs comes along: no Python, Visual Studio or CUDA Toolkit on the target PC (brief §2.5).
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import urllib.request
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DIST = ROOT / "dist"
APP = DIST / "app"
ENGINE = APP / "engine"
PY_VERSION = "3.13.14"
PY_ZIP = f"https://www.python.org/ftp/python/{PY_VERSION}/python-{PY_VERSION}-embed-amd64.zip"
PACKAGES = ["numpy", "jinja2", "regex", "pyyaml", "pillow", "psutil", "jsonschema>=4.18,<5", "tqdm", "requests"]
CUDA_BIN = Path(os.environ.get("CUDA_PATH", r"C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v13.4")) / "bin" / "x64"
CUDA_DLLS = ["cublas64_13.dll", "cublasLt64_13.dll"]
ENGINE_NAMES = {"wam": "wam-glm", "wam-ds4": "wam-ds4", "wam-mm3": "wam-mm3"}
LLAMA_EXES = ["llama-server.exe", "llama-bench.exe", "llama-perplexity.exe", "ggml-rpc-server.exe"]
ISCC = Path(os.environ["LOCALAPPDATA"]) / "Programs" / "Inno Setup 6" / "ISCC.exe"
BUILD = ROOT / "build"


def run(cmd, **kw):
    print(">", " ".join(str(c) for c in cmd), flush=True)
    subprocess.run([str(c) for c in cmd], check=True, **kw)


def publish():
    run(["dotnet", "publish", ROOT / "src/WinAIMagic.App/WinAIMagic.App.csproj", "-c", "Release", "-p:Platform=x64", "-o", APP])


def tiered():
    src = ROOT / "engine" / "tiered"
    dst = ENGINE / "tiered"
    shutil.rmtree(dst, ignore_errors=True)
    ignore = shutil.ignore_patterns("test_*", "__pycache__", "*.pyc", "fake_*", "*_fake_*", "fixtures", "chat_golden.json")
    shutil.copytree(src / "serve", dst / "serve", ignore=ignore)
    shutil.copytree(src / "tools", dst / "tools", ignore=ignore)
    shutil.copy2(src / "VERSION", dst / "VERSION")
    gguf_py = src / "third_party" / "llama.cpp" / "gguf-py"
    shutil.copytree(gguf_py, dst / "tools" / "gguf-py", ignore=shutil.ignore_patterns("tests", "__pycache__", "examples"))
    for rel in ("wam.py",):
        if (src / rel).exists():
            shutil.copy2(src / rel, dst / rel)
    bin_dir = dst / "bin"
    bin_dir.mkdir(parents=True, exist_ok=True)
    for name, target in ENGINE_NAMES.items():
        shutil.copy2(BUILD / "tiered-cuda" / f"{name}.exe", bin_dir / f"{target}.exe")
    for dll in CUDA_DLLS:
        shutil.copy2(CUDA_BIN / dll, bin_dir / dll)


def llama():
    for backend in ("cuda", "vulkan", "cpu"):
        dst = ENGINE / backend
        shutil.rmtree(dst, ignore_errors=True)
        dst.mkdir(parents=True)
        for exe in LLAMA_EXES:
            shutil.copy2(BUILD / f"llama-{backend}" / "bin" / exe, dst / exe)
        if backend == "cuda":
            for dll in CUDA_DLLS:
                shutil.copy2(CUDA_BIN / dll, dst / dll)


def python():
    dst = ENGINE / "python"
    shutil.rmtree(dst, ignore_errors=True)
    cache = DIST / "cache"
    cache.mkdir(parents=True, exist_ok=True)
    z = cache / f"python-{PY_VERSION}-embed-amd64.zip"
    if not z.exists():
        urllib.request.urlretrieve(PY_ZIP, z)
    with zipfile.ZipFile(z) as f:
        f.extractall(dst)
    site = dst / "Lib" / "site-packages"
    site.mkdir(parents=True)
    run([sys.executable, "-m", "pip", "install", "--quiet", "--disable-pip-version-check", "--no-compile",
         "--python-version", PY_VERSION, "--only-binary=:all:", "--platform", "win_amd64", "--target", site, *PACKAGES])
    pth = next(dst.glob("python*._pth"))
    pth.write_text(f"{pth.stem.replace('python', 'python')}.zip\n.\nLib\\site-packages\n..\\tiered\nimport site\n", encoding="utf-8")
    for junk in site.rglob("tests"):
        if junk.is_dir():
            shutil.rmtree(junk, ignore_errors=True)


def notices():
    parts = ["# Third party notices\n\nWinAIMagic includes the following software.\n"]
    sources = [("Local engine (fork)", ROOT / "engine/tiered/LICENSE"),
               ("llama.cpp / ggml", ROOT / "engine/llama.cpp/LICENSE"),
               ("exllamav3", ROOT / "engine/tiered/third_party/exllamav3/LICENSE"),
               ("beellama.cpp", ROOT / "engine/tiered/third_party/beellama.cpp/LICENSE"),
               ("ggml (engine)", ROOT / "engine/tiered/third_party/ggml/LICENSE"),
               ("Python", ENGINE / "python/LICENSE.txt")]
    for name, path in sources:
        if path.exists():
            parts.append(f"\n## {name}\n\n```\n{path.read_text(encoding='utf-8', errors='replace').strip()}\n```\n")
    parts.append("\n## NVIDIA CUDA Toolkit runtime libraries\n\ncuBLAS and cuBLASLt are redistributed under the NVIDIA CUDA Toolkit EULA "
                 "(Attachment A). They are used only by this application.\n")
    parts.append("\n## Python packages\n\nnumpy, jinja2, regex, pyyaml, pillow, psutil, jsonschema, tqdm, requests: "
                 "their licences are in engine\\python\\Lib\\site-packages\\*.dist-info.\n")
    (APP / "THIRD_PARTY_NOTICES.md").write_text("".join(parts), encoding="utf-8")
    (APP / "LICENSE.txt").write_text((ROOT / "LICENSE").read_text(encoding="utf-8"), encoding="utf-8")


def scan():
    """Brief §2.2: the upstream name is only in the notices, licence files and download URLs of the catalog."""
    hits = []
    allowed = ("THIRD_PARTY_NOTICES.md", "LICENSE")
    pattern = re.compile(rb"maya", re.I)
    for p in APP.rglob("*"):
        if not p.is_file() or p.name.startswith(allowed):
            continue
        if pattern.search(p.name.encode()):
            hits.append(("name", p))
        rel = p.relative_to(APP).as_posix()
        if rel.startswith("engine/python/Lib"):
            continue
        if p.suffix.lower() in (".py", ".json", ".txt", ".md", ".jinja", ".pdb", ".exe"):
            data = p.read_bytes()
            for m in pattern.finditer(data):
                ctx = data[max(0, m.start() - 14):m.end() + 6]
                if b"Flash-Maya" not in ctx:
                    hits.append(("text", p, ctx))
                    break
    for h in hits:
        print("NAME FOUND:", h)
    return hits


def setup():
    run([ISCC, f"/DSource={APP}", f"/DOut={DIST}", ROOT / "installer/WinAIMagic.iss"])


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-publish", action="store_true")
    ap.add_argument("--skip-setup", action="store_true")
    a = ap.parse_args()
    APP.mkdir(parents=True, exist_ok=True)
    if not a.skip_publish:
        publish()
    tiered()
    llama()
    python()
    notices()
    if scan():
        print("the upstream name is still in the package: see above")
    if not a.skip_setup:
        setup()
    print("done:", DIST)
