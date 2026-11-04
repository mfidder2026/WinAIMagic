# Measurements on Windows

All numbers come from one PC, measured in November 2026. Raw data: [measurements/](measurements/).

**PC:** NVIDIA GeForce RTX 5060 Ti 16 GB (driver 610.88) on PCIe 3.0 x8, Intel Core i9-10900K, 128 GB DDR4-2400 in two channels, NVMe (ADATA Legend 710), Windows 11. Page file 25.6 GB unless noted.

**Method:** the same as MacAIMagic's `BenchmarkRunner`: start the engine, read a long prompt with a fresh first line, write 300 tokens. Thinking off, 64K context. Most settings were run two or three times with 300 tokens, so read small differences as noise.

## Through the installed app

| Model | Engine | Load | Prompt | Writing | VRAM | RAM |
|---|---|---|---|---|---|---|
| Qwen3.6-35B-A3B Q4 | Second, CUDA, 23 CPU expert layers (chosen by the planner) | 15 s | 380 tok/s | 39.5 tok/s | 13.6 GB | 38 GB |
| GLM-5.3-Flash Compact 24 | Main | 98 s | 354 tok/s | 8.9 tok/s | 14 GB | 98 GB |

The first GLM load after a reboot took 127 to 156 s. It took over 300 s while other heavy work ran on the PC.

## 1. Qwen3.6: filling the card is the trap

| Backend | `--n-cpu-moe` | Prompt | Writing | VRAM |
|---|---|---|---|---|
| CUDA | 0 (all on the card) | 68 tok/s | 4.9 tok/s | Not sampled; the model is 22 GB |
| CUDA | 10 | 57 tok/s | 9.0 tok/s | 15.5 GB |
| CUDA | **20** | **246-283 tok/s** | **38.9-39.4 tok/s** | **14.0 GB** |
| CUDA | 99 (all experts on the CPU) | 133-154 tok/s | 24.5 tok/s | 4.9 GB |
| Vulkan | 99 | 84-156 tok/s | 18.9-19.9 tok/s | 4.6 GB |
| CPU only | - | 55 tok/s | 7.4 tok/s | 0.6 GB |

A card that is nearly full does not fail on Windows: the driver starts paging and the speed falls by a factor of four or more. The planner therefore keeps a margin on the card, and that matches the fastest setting found here.

CUDA beats Vulkan on this card at the same setting (24.5 against 19 tok/s).

## 2. GLM-5.3-Flash Compact 24: exact settings hardly matter

| Setting | Prompt | Writing |
|---|---|---|
| Page file 8 GB, reserve 1500 MiB | 333-344 tok/s | 8.1-8.4 tok/s |
| Page file 25.6 GB, reserve 1500 MiB | 329-345 tok/s | 8.3-8.4 tok/s |
| Reserve 700 MiB | 337-347 tok/s | 8.8 tok/s |
| RAM resident, promote minimum 6 | 340-348 tok/s | 8.6-10.1 tok/s |
| RAM resident, balanced | 340-342 tok/s | 8.2-8.6 tok/s |

- **Page file:** at 8 GB the engine warned that the RAM tier was capped 9 GB below free RAM. With 25.6 GB the warning went, but this model already fits, so the speed did not change. The difference should show on models that do not fit.
- **Calibration** (`--pcie-frac`, draft floor, CPU workers, 19 minutes) found nothing better than the defaults on this PC. The candidates it confirmed were slower: 4.9-5.0 and 7.3-8.0 tok/s.
- **Where the time goes** (engine log): about 120 ms per token, of which 56-63 ms in the CPU lane. An expert costs 0.40-0.48 ms on the CPU and 1.1 ms over PCIe, so this PC's PCIe 3.0 x8 link sends most missing experts to the CPU. 43-53% of experts hit VRAM; none came from the SSD while writing.

Writing at 8-9 tok/s is well below the 17-24 tok/s the brief estimated from faster systems. Likely causes are the PCIe link and two channels of DDR4-2400. The RAM is rated for 3200 MT/s (XMP is off); this was not measured.

## 2b. Candidates from the Strata repositories

`niko1221/strata` and its frozen copy `mach10x/strata` were compared with the engine fork. Each candidate was measured here with three runs, with the default before and after: 8.6-8.8 tok/s both times.

| Setting | Writing | Verdict |
|---|---|---|
| `KDA_GRAPH=1` | 8.0-8.2 tok/s | Slower here, left off |
| `DEFER_CPU=1` | 8.1-8.3 tok/s | Slower here, left off |
| VRAM reserve 700 MiB | 8.6-9.1 tok/s | Within the noise, and the card fills to 15.1 of 15.9 GB: left at 1500 |
| `TRUNK_KEEP_MAP=...55555554444...` | **11.2-11.5 tok/s** (+31%) | Setting "Skip experts: light", changes the answers |
| `MISS_SKIP_W=0.14`, `MISS_SKIP_FROM=23` | **12.2-12.5 tok/s** (+42%) | Setting "Skip experts: strong", changes the answers |

Through the installed app: light 11.3-11.4, strong 12.1-12.3 tok/s. Through the Compare screen, chosen beside the Start button: full model 8.9-9.0 (two runs), light 11.1-11.8 (three runs), strong 12.6 tok/s (one run).

Prompt reading did not change. The gain is large here because this PC is held back by its CPU lane and PCIe link. How much the answers change was **not measured here**. Upstream measured it on an RTX 3090 with this model: KL to the full model 0.027 for this map and 0.052 for 0.14. The settings are off by default, because they trade accuracy for speed.

Taken over as well: an engine token below 0 is now a request error (upstream `5ee1c38`). Nothing was usable for MacAIMagic: the Metal engine does not port to llama.cpp, and the one relevant change gains only on small Macs.

## 3. The second model beside the heavy one

GLM through the app, Qwen3.6 on the CPU next to it, 16K context.

| | Writing |
|---|---|
| Qwen on the CPU, GLM idle | 7.8 tok/s |
| GLM, Qwen idle | 8.8 tok/s |
| Both at once: Qwen | 5.0 tok/s |
| Both at once: GLM | 7.0 tok/s |

Free RAM: 31 GB with GLM alone, 6 GB with both. It works on 128 GB, narrowly. The app allows it when free RAM is at least the small model plus 8 GB; otherwise one model does everything.

## 4. Real tasks

All with GLM-5.3-Flash Compact 24 through the installed app, using `RealEngineTests` (set `WAM_REAL_URL`).

- **Agent task in a work folder:** write a Python module and its tests, run them, write the primes up to 50 to a file. Done in 2.0-2.2 minutes and 5 rounds, with the correct output. In one run the model noticed that Windows PowerShell 5.1 has no `&&` and adapted by itself.
- **Summarizing:** a three-question conversation was summarized, and the model still answered from it afterwards.
- **Document pipeline:** a short note in the house style from the fixture `sjabloon.docx`: 3.5 minutes, no validator problems, 4 pages. It opens in Microsoft Word with cover, working table of contents and headings. Fields nobody filled in stay visible and are reported. The content comes from the model and needs checking: this draft named a law that does not seem to exist.
- **APIs:** OpenAI, Anthropic (with a tool) and Responses all answered. A prompt beyond the context gives a 400 that names the context.
- **Thinking budget per request:** budget 0 gives no reasoning; a budget of 120 tokens closed the thinking 26 tokens later at a sentence end, with the closing message in the reasoning and the answer after it.
- **Forced close of the app:** no process of the installation stays behind, measured several times.

- **A session that fills the 64K context:** 30 files of about 3,000 tokens each, one code to find in each, and every code to be added to a file as it goes. Full model, 33.5 minutes, 130 messages, 63 tool calls. The app summarized on the way and finished; all 30 codes were correct, and the model checked the result by itself at the end.
- **The summary in that session** started as a continuation of the conversation instead of a summary. A separate test on a long transcript gave proper summaries in four of four runs, so it does not always happen. The request now ends with a closing line, and the app asks once more if the answer still carries the transcript's labels. Neither has run in a whole new long session.
- **Images:** a test picture pasted with real Ctrl+V into the installed app. GLM answered with the right text and shape. Qwen3.6 did too through the API. On this 16 GB card GLM's encoder runs on the CPU, about 15 s per new picture.
- **Cluster:** a real helper on this PC (the CPU build of the RPC server, 127.0.0.1), added by hand in the app. The app read its memory (137 GB) and put 39 of 40 layers on it, which took 20.5 GB there. Qwen3.6 answered at 9.7 tok/s. It was also found through DNS-SD. Two separate PCs have not been run.
- **Real input:** Ctrl+V with an image file, a bitmap and text, Ctrl+Enter, dragging a file in from another program, and the file and folder dialogs, all with real keyboard and mouse input.
- **Skipping experts:** the same agent task succeeded in both modes, in 2.4 minutes (light) and 1.7 minutes (strong).

## 5. Engine build

Windows builds with Visual Studio 2022 Build Tools and CUDA 13.4 for `sm_75;80;86;89;120` worked: RTX 20 and newer on one binary. CUDA 13 cannot build Volta or Pascal. The multi-architecture GLM engine is 409 MB against 101 MB for one architecture.

## Licences checked for distribution

Reading of the licence texts, not legal advice.

- **cuBLAS and cuBLASLt:** listed as redistributable in Attachment A of the CUDA Toolkit EULA. Conditions include that the application adds material functionality and that only it uses them.
- **Qwen community licence:** use, distribution and modification are allowed. A separate licence is needed if the licensee conducts a Model-as-a-Service or "AI Work Assistant" business and uses the software commercially; purely internal use is exempt. Very large products must display the model name. The app shows the licence before download and does not ship the model.
- **MiniMax community licence:** free for non-commercial use. Commercial use requires "Built with MiniMax M3.1-Flash" and a one-time notice (written permission above $20M yearly revenue), and there is a list of prohibited uses. The app shows the licence before download and does not ship the model.
- **DeepSeek and GLM:** MIT.
- **Engine fork, llama.cpp, Python:** their licence texts are in `THIRD_PARTY_NOTICES.md`.
