# Parity with MacAIMagic 1.9.5

How this version relates to MacAIMagic at commit `9b3d166`. Test cases are listed one by one in [PARITY-tests.md](PARITY-tests.md).

## Swift files and their counterparts

| Swift | Windows | Status |
|---|---|---|
| `MacAIMagicApp`, `AppState` | `App/App.xaml.cs`, `App/AppState.cs`, `App/ShellWindow.cs` | Done |
| `Chat/ChatClient`, `ChatModels` | `Chat/ChatClient`, `ChatModels`, `ChatTexts`, `StreamThrottle` | Done, tested against real GLM |
| `Chat/WorkspaceTools` | `Chat/WorkspaceTools`, `CommandRunner`, `KillOnCloseJob` | Done, sandbox extended for Windows |
| `Chat/WebTools`, `WorkspaceNotes` | Same names | Done; web parsers tested on fixtures only |
| `Documents/Zip`, `XNode`, `DocxPackage`, `DocxModel`, `DocxBuilder`, `DocxFields`, `DocxCompose`, `MarkdownBlocks` | `Documents/` same names | Done |
| `Documents/DocumentStudio`, `DocumentModels` | `Studio/` same names | Done; one real document on GLM, the fast model beside it only measured by hand |
| `Engine/ModelCatalog`, `GGUFIndex`, `ModelStore` | `Engine/` same names, `Downloader`, `PackBuilder` | Done |
| `Engine/MemoryPlanner` | `Engine/MemoryPlanner` | Redesigned for VRAM + RAM + SSD |
| `Engine/EngineController` | `Engine/IEngine`, `TieredEngine`, `LlamaEngine`, `ChildProcess` | Redesigned, two engines |
| `Engine/ClusterController` | `Engine/ClusterController` | Done; real helpers and DNS-SD not run |
| `Engine/BenchmarkRunner` | `Engine/BenchmarkRunner` | Done, plus VRAM, RAM and export |
| `Support/LiveMetrics`, `SystemInfo`, `SystemCheck` | `Engine/` same names, `Engine/Gpu/*` | Done; counters are locale independent |
| `Support/Settings`, `Paths`, `Localization`, `Translations` | `Core/Support/*`, `Core/i18n/*.json` | Done |
| `Views/*` | `App/Views/*` | Done, in code rather than XAML |

## Deliberate differences

- **GPU limit button:** removed, it has no Windows counterpart. The page file check takes its place.
- **Model files on disk** get a neutral name; the download servers keep theirs.
- **Moving models:** the Mac only points at another folder. Here the app also moves the files, resumable and verified.
- **Verwijder model** was the one added feature in the brief. The owner also asked for **skipping experts on GLM** (faster, answers change a little; off by default; see RESEARCH.md).
- **File and folder dialogs** use IFileOpenDialog directly: the WinRT folder picker failed with E_FAIL from this unpackaged app.
- **Stop for the main engine** closes its parent pipe rather than sending a console break, because the engine would otherwise lose its expert profile.
- **pwsh from the Microsoft Store** is skipped for `run_command`: its children leave the Job Object. Windows PowerShell 5.1 runs instead.

## Mac gaps fixed here (brief §11)

| Mac behaviour | Here |
|---|---|
| A `&` process keeps the pipe open past the timeout | The timeout always works; detached programs keep running |
| Stop leaves a running command going | Stop ends the process tree |
| A `tool_call` without a result after Stop | Result "gestopt door de gebruiker" |
| Pause message on an invisible message | Visible in the chat |
| Download errors shown nowhere | On the model card |
| `engine.log` holds unfiltered upstream names | The log is filtered |
| The helper keeps running after quitting | Stops along, through a Job Object |
| An unreadable `conversations.json` is overwritten | Kept as `.bad` and reported |
| A Brave failure looks like "no results" | A message, then DuckDuckGo |
| API key visible in process arguments | In the config or environment |
| Search region always Dutch | Follows the app's language |

The same gaps still exist in MacAIMagic.

## Not done yet

**Features**

- **AMD:** no HIP engine ships, so AMD cards use the second engine through Vulkan (limited mode).
- **Images on DeepSeek and MiniMax:** wired (their vision tower goes into the pack, the engine runs it), but those models are not on the reference PC, so this has not run with a real image. GLM on the main engine and Qwen3.6 on the second engine have.
- **GLM images on a 16 GB card:** the expert pool cannot lend the encoder its 2.8 GB, so the encoder stays loaded and runs on the CPU, about 15 s per new picture against 0.14 s on the GPU. Lending on a larger card has not been run; the GPU encoder has only run on its own, on an RTX 5060 Ti (sm_120a), the other architectures are built but not run.
- **NVIDIA below RTX 20:** the toolkit used here (CUDA 13) cannot build Volta or Pascal.
- **Two heavy-and-fast models:** the heavy engine is not restarted with a lower RAM tier to make room. If the fast model does not fit beside it, one model does everything.
- **Idle unload** of the model has no setting yet.
- **Live page:** pinned RAM and the VRAM/RAM/SSD split come from the plan, not from engine telemetry.
- **Test allocation** when choosing a backend is missing; a failing driver shows up at engine start.

**Never run on real hardware**

- A clean Windows 11 PC, and any PC other than the one test PC.
- DeepSeek, MiniMax, Qwen3.8 and the larger GLM models.
- The page file button: a dry run checked the commands, and the change itself needs someone to accept the UAC prompt.
- A real move of models; downloading through the app beyond a short test.
- A cluster of two separate PCs: the helper has run on the same PC only, and on its CPU build.
- The documents file dialogs and the models folder dialog past the chosen folder (the same code as the composer's, which ran).
- Images with skipped experts.
- Claude Code itself: the Anthropic API was tested directly, with and without tools.
- Web search and quick actions in the documents editor against a real model.
- Word was checked on two generated documents, by opening them and looking at the result, not with a comparison against the template.

**Release**

- The installer is not code-signed, so SmartScreen warns.
- The upstream name remains in the licence text, in model file names on download servers, in the log filter and in the original brief `WinAIMagic-PROMPT.md`.
- No separate zip is published next to the installer.
