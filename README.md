<p align="center">
  <img src="docs/screenshots/icon.png" width="96" alt="WinAIMagic icon">
</p>

<h1 align="center">WinAIMagic</h1>

<p align="center">
  <strong>Run huge AI models entirely on your own Windows 11 PC.</strong><br>
  Models that do not fit in your video card simply stream from RAM and SSD. Nothing leaves your computer.
</p>

<p align="center">
  <a href="https://github.com/mfidder2026/WinAIMagic/releases/latest">Download the installer</a> ·
  <a href="docs/README.nl.md">Nederlands</a>
</p>

![The chat screen with GLM-5.3-Flash running locally](docs/screenshots/chat.png)

WinAIMagic is the Windows version of [MacAIMagic](https://github.com/mfidder2026/MacAIMagic). Install it with one setup file: no Python, no Visual Studio and no CUDA Toolkit needed, just a video card driver.

## What it does

- **Chat** with thinking limits, images, web search, and a context meter that summarizes before the model runs out of room. Images have been run on GLM and Qwen3.6; DeepSeek and MiniMax are wired but not yet run with a real image, and each model fetches its image files separately (about 1 to 1.7 GB).
- **Work in a folder.** The model reads and writes files and runs commands there, with your approval per kind of action (read, write, run). It keeps going until the job is done and remembers what it learned.
- **Documents.** Turn notes and attachments into a Word file in your own house style, section by section, with sources. You review the draft before the `.docx` is written.
- **Compare** models with the same measurement on your own PC, and export the results.
- **A local API** on `http://127.0.0.1:8080`: OpenAI (`/v1`), Anthropic (`/v1/messages`) and Responses (`/v1/responses`), so other tools can use your model.
- **Choose where models live**, on any drive, and move them later without downloading again.
- **Cluster** several PCs over USB4, Thunderbolt or ethernet for models on the second engine.

Available in English, Dutch, German and French.

## Screens

| Models | Live dashboard |
|---|---|
| ![Models](docs/screenshots/models.png) | ![Live](docs/screenshots/live.png) |
| **Documents** | **System check** |
| ![Documents](docs/screenshots/documents.png) | ![System check](docs/screenshots/system.png) |

## Install

1. Download `WinAIMagic-Setup-*.exe` from [Releases](https://github.com/mfidder2026/WinAIMagic/releases/latest) (about 1.6 GB).
2. Run it. Choose a folder for the models, ideally on a fast NVMe drive. No administrator rights are needed.
3. Windows SmartScreen may warn you, because this installer is not code-signed yet: choose **More info** and **Run anyway**. The `.sha256` next to the installer lets you check the file.
4. In the app, open **Models**, download a model, and press **Start engine**.

The system check tells you what your PC still needs. The most common one: the Windows page file must be at least your VRAM plus 9 GB. The app offers a button for that; it asks for permission and needs a restart.

## How large models fit

A Mac has one memory pool. A PC has three layers: VRAM, RAM and SSD. WinAIMagic plans over all three and starts the model anyway when it does not fit: the experts used most stay in VRAM and RAM, and the rest stream from the SSD. A model is never called "too big".

Two engines do the work. The main engine handles large mixture-of-experts models across the three layers. The second engine, based on llama.cpp, runs smaller models, the cluster, and PCs without a supported card.

| Video card | Main engine | Second engine |
|---|---|---|
| NVIDIA RTX 20 series and newer | Yes | CUDA |
| AMD | Not in this version | Vulkan |
| Intel Arc and others | No | Vulkan |
| No usable card | No | CPU |

## Measured speed

Measured with the app's own method: a 4,000-token prompt read, then 300 tokens written, 64K context.
**PC: RTX 5060 Ti 16 GB on PCIe 3.0 x8, Core i9-10900K, 128 GB DDR4-2400 in two channels, NVMe. Windows 11.**

| Model | Load | Prompt reading | Writing | VRAM | RAM |
|---|---|---|---|---|---|
| Qwen3.6-35B-A3B Q4 (second engine) | 15 s | 380 tok/s | **39.5 tok/s** | 13.6 GB | 38 GB |
| GLM-5.3-Flash Compact 24 (main engine) | 98 s | 354 tok/s | **8.9 tok/s** | 14 GB | 98 GB |

Your numbers depend mostly on your CPU, your memory channels and your video card's link, so measure on your own PC under **Compare**. Every other model in the catalog shows "not measured" until you do. This is one PC with one video card; AMD has not been measured at all.

## Models

| Model | Download | Licence |
|---|---|---|
| Qwen3.6-35B-A3B Q4 | 22 GB | Qwen community licence, shown before download |
| GLM-5.3-Flash Compact 24 | 95 GB | MIT |
| MiniMax-M3.1-Flash Compact | 93 GB | MiniMax community licence, shown before download |
| GLM-5.3-Flash Compact | 109 GB | MIT |
| Qwen3.8-Flash-Next Q4 | 111 GB | Qwen community licence, shown before download |
| GLM-5.3-Flash Q4 | 200 GB | MIT |
| DeepSeek-V4.1-Flash Compact | 277 GB | MIT |
| DeepSeek-V4.1-Flash Q4 | 459 GB | MIT |

Read the model licences before using a model commercially. Only Qwen3.6 and GLM-5.3-Flash Compact 24 have been run on the test PC so far.

## Privacy

Only web search and model downloads go out, and web search can be turned off. There is no telemetry and no update check.

## Build from source

You need Visual Studio 2022 Build Tools with C++, the CUDA Toolkit, CMake, Ninja, the Vulkan SDK, the .NET 10 SDK, Python 3 and Inno Setup.

```bash
sh scripts/fetch-llama-tiered.sh
sh scripts/fetch-llama-engine.sh
scripts\build-tiered-cuda.bat "75;80;86;89;120"
scripts\build-vision-cuda.bat "75;80;86;89;120"
scripts\build-llama.bat cuda
scripts\build-llama.bat vulkan
scripts\build-llama.bat cpu
python scripts/package.py
```

Tests: `dotnet test tests/WinAIMagic.Chat.Tests -c Debug -p:Platform=x64`, and likewise for `Engine`, `Documents` and `Studio`.

See [docs/PARITY.md](docs/PARITY.md) for how this version relates to MacAIMagic and what is still open, and [docs/RESEARCH.md](docs/RESEARCH.md) for the measurements.

## Licence

MIT, see [LICENSE](LICENSE). Included third-party software is listed in `THIRD_PARTY_NOTICES.md`, which is also in the installation folder.
