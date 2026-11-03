#!/usr/bin/env python3
"""Phase 0 measurement, the same method as the Mac app's BenchmarkRunner: start the engine, read a long prompt, write 300
tokens, twice (cold and warm). Needs no packages. Results are appended to docs/measurements/<date>.jsonl.

  measure.py glm  --models DIR [--context 65536] [--reserve 1500] [--env K=V ...] [--runs 3]
  measure.py qwen --models DIR [--context 65536] [--backend cuda|vulkan|cpu] [--ngl 99] [--runs 3]
"""
import argparse
import json
import os
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
PORT = 8099
BASE = f"http://127.0.0.1:{PORT}"
TOPICS = ["de geschiedenis van de Nederlandse waterbouw", "hoe een mixture-of-experts taalmodel werkt",
          "de werking van geheugen in een videokaart", "het klimaat in de Waddenzee",
          "de ontwikkeling van open-source software", "de natuurkunde van zonnepanelen"]
GROUPS = ["regio's", "bedrijven", "studenten", "gemeenten"]


def long_prompt() -> str:
    """About 2.5K tokens; a fresh first line each run, so the prompt cache never hides the reading cost."""
    text = f"[{time.time()}] Lees de volgende notities en vat ze samen in drie zinnen.\n\n"
    for i in range(36):
        text += f"Notitie {i + 1}: over {TOPICS[i % 6]}. Dit onderwerp raakt aan techniek, economie en maatschappij. "
        text += f"Onderzoekers noemen in rapport {1000 + i * 7} vooral de kosten, de betrouwbaarheid en de gevolgen op lange termijn. "
        text += f"In {1950 + i} werd een belangrijke stap gezet, en sindsdien zijn er {i * 3 + 2} grote verbeteringen geweest. "
        text += f"Critici wijzen op risico's; voorstanders op kansen voor {GROUPS[i % 4]}.\n"
    return text


def complete(prompt: str, max_tokens: int) -> dict:
    body = {"messages": [{"role": "user", "content": prompt}], "max_tokens": max_tokens, "temperature": 0.7,
            "chat_template_kwargs": {"enable_thinking": False}, "thinking_budget_tokens": 0}
    req = urllib.request.Request(BASE + "/v1/chat/completions", json.dumps(body).encode(),
                                 {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=1800) as r:
        return json.load(r)


class Sampler(threading.Thread):
    """VRAM and system RAM use while running, plus the disk's read rate."""

    def __init__(self):
        super().__init__(daemon=True)
        self.stopped, self.samples = threading.Event(), []

    def run(self):
        while not self.stopped.wait(2):
            try:
                vram = float(subprocess.check_output(
                    ["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"], text=True).split()[0])
                ps = subprocess.check_output(
                    ["powershell", "-NoProfile", "-Command",
                     "$o=Get-CimInstance Win32_OperatingSystem; ($o.TotalVisibleMemorySize-$o.FreePhysicalMemory)/1MB;"
                     # (performance counter names are translated on a non-English Windows: WMI's are not)
                     "(Get-CimInstance Win32_PerfFormattedData_PerfDisk_PhysicalDisk -Filter \"Name='_Total'\")"
                     ".DiskReadBytesPersec/1MB"],
                    text=True).split()
                self.samples.append({"t": time.time(), "vram_mib": vram, "ram_gb": float(ps[0].replace(",", ".")),
                                     "disk_mbs": float(ps[1].replace(",", "."))})
            except (OSError, subprocess.SubprocessError, ValueError, IndexError):
                pass

    def summary(self, since: float) -> dict:
        s = [x for x in self.samples if x["t"] >= since]
        if not s:
            return {}
        return {"vram_gb_max": round(max(x["vram_mib"] for x in s) / 1024, 1),
                "ram_gb_max": round(max(x["ram_gb"] for x in s), 1),
                "disk_mbs_avg": round(sum(x["disk_mbs"] for x in s) / len(s))}


def glm_command(a, log_dir: Path):
    m = Path(a.models) / "glm-5.3-flash-compact-24"
    pack, profile = m / "pack", m / "expert-profile.bin"
    cfg = {"exe": str(ROOT / "build/tiered-cuda/maya.exe"),
           "args": ["--glm-pack", str(pack), "--max-context", str(a.context), "--expert-cache", "auto", "--prefill", "auto",
                    "--kv-streaming", "on", "--expert-profile-save", str(profile),
                    *(["--expert-profile", str(profile)] if profile.exists() else []),
                    "--vram-reserve-mib", str(a.reserve)],
           "cwd": str(ROOT / "engine/tiered"), "tokenizer": str(pack / "tokenizer"), "model_name": "glm-5.3-flash-compact-24",
           "gpu": [0], "sampling": {"temperature": 1.0, "top_p": 0.95}, "reasoning_effort": "low", "lib_dirs": [],
           "port": PORT, "log": str(log_dir / "maya-engine.log"), "env": dict(e.split("=", 1) for e in a.env)}
    path = log_dir / "glm.json"
    path.write_text(json.dumps(cfg, indent=1), encoding="utf-8")
    return [sys.executable, "-m", "serve.server", "--engine", "maya", "--config", str(path), "--port", str(PORT)], \
        ROOT / "engine/tiered"


def qwen_command(a, log_dir: Path):
    gguf = Path(a.models) / "qwen3.6-35b-a3b-q4" / "qwen3.6-35b-a3b-q4.gguf"
    return [str(ROOT / f"build/llama-{a.backend}/bin/llama-server.exe"), "-m", str(gguf), "-c", str(a.context),
            "-ngl", str(a.ngl), "--port", str(PORT), "--host", "127.0.0.1", "--jinja", *a.extra.split()], ROOT


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("model", choices=["glm", "qwen"])
    ap.add_argument("--models", default=os.path.join(os.environ.get("LOCALAPPDATA", ""), "WinAIMagic", "models"))
    ap.add_argument("--context", type=int, default=65536)
    ap.add_argument("--reserve", type=int, default=1500)
    ap.add_argument("--backend", default="cuda")
    ap.add_argument("--ngl", type=int, default=99)
    ap.add_argument("--env", action="append", default=[])
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--extra", default="", help="more llama-server arguments as one string: --extra=\"--n-cpu-moe 20\" (qwen)")
    a = ap.parse_args()

    log_dir = Path(tempfile.mkdtemp(prefix="wam-measure-"))
    cmd, cwd = (glm_command if a.model == "glm" else qwen_command)(a, log_dir)
    env = dict(os.environ, PYTHONUTF8="1", **dict(e.split("=", 1) for e in a.env))
    log = log_dir / "engine.log"
    print("log:", log, flush=True)
    sampler = Sampler()
    sampler.start()
    t0 = time.time()
    with open(log, "wb") as lf:
        proc = subprocess.Popen(cmd, cwd=cwd, env=env, stdout=lf, stderr=subprocess.STDOUT)
    result = {"model": a.model, "date": time.strftime("%Y-%m-%d %H:%M"), "context": a.context, "reserve": a.reserve,
              "backend": a.backend, "ngl": a.ngl, "env": a.env, "extra": a.extra, "runs": []}
    try:
        while True:
            if proc.poll() is not None:
                raise SystemExit(f"the engine stopped with code {proc.returncode}; see {log}")
            if time.time() - t0 > 1800:
                raise SystemExit(f"not ready after 30 minutes; see {log}")
            try:
                with urllib.request.urlopen(BASE + "/health", timeout=5) as r:
                    if r.status == 200:
                        break
            except (urllib.error.URLError, OSError):
                pass
            time.sleep(2)
        result["load_s"] = round(time.time() - t0, 1)
        print("ready after", result["load_s"], "s", flush=True)
        for i in range(a.runs):
            start = time.time()
            p = complete(long_prompt(), 16).get("timings", {})
            d = complete("Schrijf een verhaal van 300 woorden over een vuurtorenwachter op Terschelling.", 300)
            t = d.get("timings", {})
            run = {"run": i + 1, "prompt_tps": round(p.get("prompt_per_second", 0), 1), "prompt_n": p.get("prompt_n"),
                   "decode_tps": round(t.get("predicted_per_second", 0), 2), "decode_n": t.get("predicted_n"),
                   **sampler.summary(start)}
            result["runs"].append(run)
            print(json.dumps(run), flush=True)
        text = d["choices"][0]["message"].get("content", "")
        result["sample"] = text[:300]
        print("sample:", text[:200], flush=True)
    finally:
        sampler.stopped.set()
        if proc.poll() is None:
            proc.terminate()
            try:
                proc.wait(60)
            except subprocess.TimeoutExpired:
                proc.kill()
        out = ROOT / "docs" / "measurements"
        out.mkdir(parents=True, exist_ok=True)
        with open(out / (time.strftime("%Y-%m-%d") + ".jsonl"), "a", encoding="utf-8") as f:
            f.write(json.dumps(result, ensure_ascii=False) + "\n")


if __name__ == "__main__":
    main()
