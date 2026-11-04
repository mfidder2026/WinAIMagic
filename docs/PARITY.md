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
- **Verwijder model** is the one added feature.
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
- **Images on the main engine:** vision is not wired for GLM, DeepSeek and MiniMax on the main engine. The second engine has the path, not run with a real image.
- **NVIDIA below RTX 20:** the toolkit used here (CUDA 13) cannot build Volta or Pascal.
- **Two heavy-and-fast models:** the heavy engine is not restarted with a lower RAM tier to make room. If the fast model does not fit beside it, one model does everything.
- **Idle unload** of the model has no setting yet.
- **Live page:** pinned RAM and the VRAM/RAM/SSD split come from the plan, not from engine telemetry.
- **Test allocation** when choosing a backend is missing; a failing driver shows up at engine start.

**Never run on real hardware**

- A clean Windows 11 PC, and any PC other than the one test PC.
- DeepSeek, MiniMax, Qwen3.8 and the larger GLM models.
- The page file button (UAC), a real move of models, real cluster helpers, downloading through the app beyond a short test.
- Drag and drop, real Ctrl+V, Ctrl+Enter and the file and folder pickers (run only through test hooks).
- Claude Code itself: the Anthropic API was tested directly, with and without tools.
- A long agent session that fills the 64K context. The real task ran 5 rounds and 2 minutes; summarizing ran on a short conversation.
- Web search and quick actions in the documents editor against a real model.
- Word was checked on two generated documents, by opening them and looking at the result, not with a comparison against the template.

**Release**

- The installer is not code-signed, so SmartScreen warns.
- The upstream name remains in the licence text, in model file names on download servers, in the log filter and in the original brief `WinAIMagic-PROMPT.md`.
- No separate zip is published next to the installer.
