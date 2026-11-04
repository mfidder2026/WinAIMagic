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

## 2. GLM-5.3-Flash Compact 24: settings hardly matter

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

REAL_TASKS

## 5. Engine build

Windows builds with Visual Studio 2022 Build Tools and CUDA 13.4 for `sm_75;80;86;89;120` worked: RTX 20 and newer on one binary. CUDA 13 cannot build Volta or Pascal. The multi-architecture GLM engine is 409 MB against 101 MB for one architecture.

## Licences checked for distribution

Reading of the licence texts, not legal advice.

- **cuBLAS and cuBLASLt:** listed as redistributable in Attachment A of the CUDA Toolkit EULA. Conditions include that the application adds material functionality and that only it uses them.
- **Qwen community licence:** use, distribution and modification are allowed. A separate licence is needed if the licensee conducts a Model-as-a-Service or "AI Work Assistant" business and uses the software commercially; purely internal use is exempt. Very large products must display the model name. The app shows the licence before download and does not ship the model.
- **MiniMax community licence:** free for non-commercial use. Commercial use requires "Built with MiniMax M3.1-Flash" and a one-time notice (written permission above $20M yearly revenue), and there is a list of prohibited uses. The app shows the licence before download and does not ship the model.
- **DeepSeek and GLM:** MIT.
- **Engine fork, llama.cpp, Python:** their licence texts are in `THIRD_PARTY_NOTICES.md`.
