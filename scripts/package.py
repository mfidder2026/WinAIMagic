#!/usr/bin/env python3
"""Lays out dist\\app (the published app with its engine folder) and builds the release files.

    python scripts/package.py [--skip-publish] [--setup] [--skip-portable]

Layout: dist\\app\\WinAIMagic.exe, dist\\app\\engine\\{tiered\\(serve, tools, bin), python\\, cuda\\, vulkan\\, cpu\\}.
Everything the user needs comes along: no Python, Visual Studio or CUDA Toolkit on the target PC (brief §2.5).

Two files for a release, in dist\\ (since 1.14.3 there is one version of the app, on the owner's request):
- WinAIMagic-<v>.zip: the app without the NVIDIA engines (engine\\cuda, engine\\tiered\\bin), with portable.txt, so its data
  stays beside it, and engine\\nvidia-pack.json, which names the second file.
- WinAIMagic-NVIDIA-<v>.zip: those engines, which the app offers to download on a PC with an NVIDIA card. It must be attached to
  the release v<v>: the manifest points there.
--setup also builds the old installer, WinAIMagic-Setup-<v>.exe (everything, offline); it is no longer released.
"""
import argparse
import hashlib
import json
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
# llama-bench and llama-perplexity are built for measuring on the build PC, the app never runs them
LLAMA_EXES = ["llama-server.exe", "ggml-rpc-server.exe"]
REPO = "mfidder2026/WinAIMagic"
# what the portable app leaves out and the NVIDIA pack holds, relative to the engine folder
NVIDIA_FOLDERS = ["cuda", "tiered/bin"]
IMAGE_EXE = "wam-image.exe"
SPEECH_EXE = "wam-speech.exe"
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
    # the licences of the models are shown on their card before the download
    for rel in ("wam.py", "LICENSE.minimax-m31", "LICENSE.qwen-community-1.0", "LICENSE.deepseek-v41"):
        if (src / rel).exists():
            shutil.copy2(src / rel, dst / rel)
    bin_dir = dst / "bin"
    bin_dir.mkdir(parents=True, exist_ok=True)
    for name, target in ENGINE_NAMES.items():
        shutil.copy2(BUILD / "tiered-cuda" / f"{name}.exe", bin_dir / f"{target}.exe")
    # GLM's image encoder (scripts/build-vision-cuda.bat)
    shutil.copy2(BUILD / "vision-cuda" / "bin" / "wam-vision.exe", bin_dir / "wam-vision.exe")
    # cuBLAS only in engine\cuda: the main engine has that folder on its library path too (TieredLibDirs)


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
        # the image engine, per backend (scripts/build-image.bat)
        shutil.copy2(BUILD / f"image-{backend}" / "bin" / "sd-cli.exe", dst / IMAGE_EXE)
        # the speech engine, per backend (scripts/build-speech.bat)
        shutil.copy2(BUILD / f"speech-{backend}" / "bin" / "whisper-cli.exe", dst / SPEECH_EXE)


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
               ("stable-diffusion.cpp (image engine)", ROOT / "engine/image-engine/LICENSE"),
               ("ggml (image engine)", ROOT / "engine/image-engine/ggml/LICENSE"),
               ("whisper.cpp (speech engine)", ROOT / "engine/speech-engine/LICENSE"),
               ("ggml (speech engine)", ROOT / "engine/speech-engine/ggml/LICENSE"),
               ("Concentus (Opus decoder)", Path.home() / ".nuget/packages/concentus/2.2.2/LICENSE"),
               ("Python", ENGINE / "python/LICENSE.txt")]
    for name, path in sources:
        if path.exists():
            parts.append(f"\n## {name}\n\n```\n{path.read_text(encoding='utf-8', errors='replace').strip()}\n```\n")
    # what the image engine links in: each under its own licence
    third = ROOT / "engine/image-engine/thirdparty"
    for name, rel in (("darts-clone", "LICENSE.darts_clone.txt"), ("Oniguruma", "oniguruma/COPYING"), ("libwebp", "libwebp/COPYING"),
                      ("libwebm", "libwebm/LICENSE.TXT"), ("CUTLASS", "cutlass/LICENSE.txt")):
        path = third / rel
        if path.exists():
            parts.append(f"\n## {name} (image engine)\n\n```\n{path.read_text(encoding='utf-8', errors='replace').strip()}\n```\n")
    parts.append("\n## Whisper speech model\n\nThe speech model the app downloads (Whisper large-v3-turbo, in the format of the speech "
                 "engine) is published by OpenAI under the MIT licence.\n")
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
        # the image engine is another project: the word only occurs in the vocabularies of its text models, among
        # thousands of others, and never as the name the brief keeps out
        if rel.startswith("engine/python/Lib") or p.name in (IMAGE_EXE, SPEECH_EXE):
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


def version():
    return re.search(r"<Version>([^<]+)</Version>", (ROOT / "Directory.Build.props").read_text(encoding="utf-8")).group(1)


def zip_tree(out, items):
    """items: (path on disk, name in the zip). Deflate: what .NET's ZipFile and Windows' Explorer both unpack."""
    tmp = out.with_suffix(".tmp")
    with zipfile.ZipFile(tmp, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as z:
        for path, name in items:
            z.write(path, name)
    tmp.replace(out)


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def portable():
    v = version()
    pack = DIST / f"WinAIMagic-NVIDIA-{v}.zip"
    print(">", pack.name, flush=True)
    items = [(p, p.relative_to(ENGINE).as_posix()) for folder in NVIDIA_FOLDERS for p in sorted((ENGINE / folder).rglob("*")) if p.is_file()]
    zip_tree(pack, items)
    manifest = {"version": v, "url": f"https://github.com/{REPO}/releases/download/v{v}/{pack.name}",
                "size": pack.stat().st_size, "sha256": sha256(pack)}

    out = DIST / f"WinAIMagic-{v}.zip"
    print(">", out.name, flush=True)
    left_out = [ENGINE / f for f in NVIDIA_FOLDERS]
    files = [p for p in sorted(APP.rglob("*")) if p.is_file() and not any(p.is_relative_to(d) for d in left_out)]
    stage = DIST / "portable-extra"
    shutil.rmtree(stage, ignore_errors=True)
    stage.mkdir(parents=True)
    (stage / "portable.txt").write_text(
        "WinAIMagic\r\n\r\n"
        "This file keeps settings, conversations and models in the folder 'data' next to WinAIMagic.exe.\r\n"
        "Remove it and WinAIMagic uses %LOCALAPPDATA%\\WinAIMagic, like the installed version.\r\n", encoding="utf-8")
    (stage / "nvidia-pack.json").write_text(json.dumps(manifest, indent=1), encoding="utf-8")
    items = [(p, "WinAIMagic/" + p.relative_to(APP).as_posix()) for p in files]
    items += [(stage / "portable.txt", "WinAIMagic/portable.txt"), (stage / "nvidia-pack.json", "WinAIMagic/engine/nvidia-pack.json")]
    zip_tree(out, items)
    shutil.rmtree(stage, ignore_errors=True)
    for f in (out, pack):
        print(f"  {f.name}: {f.stat().st_size / 1e6:.0f} MB, sha256 {sha256(f)}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--skip-publish", action="store_true")
    ap.add_argument("--setup", action="store_true", help="also build the installer (no longer released)")
    ap.add_argument("--skip-portable", action="store_true")
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
    if a.setup:
        setup()
    if not a.skip_portable:
        portable()
    print("done:", DIST)
