# Parity with MacAIMagic 1.9.12

How this version relates to MacAIMagic at commit `82723b8`, the last Mac commit taken over. [CLAUDE.md](../CLAUDE.md) says how to take over newer ones. Test cases are listed one by one in [PARITY-tests.md](PARITY-tests.md).

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

## Taken over since the first port

### Up to `82723b8` (1.9.12): delete a model, recognise a newer version

- **Taken over, `fcae4fa`, Update:** the .ok beside a verified file already held its sha256 here, but only its existence was checked. Now one that differs from the catalog's, or a size that no longer matches, means another version: the card (and Beeld for the image model) says so in amber with how much comes down, and offers **Update**, which downloads only the files that differ. Until then the old version keeps working. The old file is deleted before the new one comes down, as on the Mac, so the model does not work during the download; the button says so and is grey while the model runs. A pack built from the old files is built again by its own staleness check. The Mac's test names (TheCatalogsVersion, AnotherVersionIsAnUpdate, TheOkIsReadFromDisk), plus AnUpdateFetchesOnlyTheFileThatDiffers against the fake server.
- **Taken over, `fcae4fa`, not deleting a running model:** Verwijder model was already here, but greyed only for the main engine. Like the Mac it is now grey too while the model runs as the fast model on the second engine. Before, the delete went through and that engine went on running a model that was gone.
- **Adapted, Controleer:** not taken over as a button. Windows has written the sha256 into the .ok from its first version, so it has no empty .ok files of its own. An empty one (a model folder copied from an older Mac) is trusted, as on the Mac before Controleer; the test is AnOldOkIsTrusted.
- **Measured here:** the Debug build with a Ministral whose .ok named another version showed Update and the amber line (5 GB); with Ministral running as the fast model its Verwijder model was grey, the heavy model's was not. Update itself was run against the fake server only, not against the real download.
- **Not needed:** `942aead` and `e1bbe1f` (the share button came from here), `246f8f9` (a take-over from here), `50cb728` and `82723b8` (Mac versions).

### `008ab2f` (1.9.11): an English, name-first description of only what can be seen

- **Taken over as it is:** the Mac's instruction word for word: English whatever the user wrote, the subject's common English name first, only what can be seen (no measurements, dates, history or construction), at most 60 words. An answer that is not English is asked for once more. When the model searched but read no page, the app reads the first page with text and an encyclopedia (no stock-photo sites) and has it rewrite from those. The Mac's test names.
- **Measured here** with Ministral as the fast model (on the processor) and "maak een foto van de eifeltoren" at 768 x 768: it searched, read en.wikipedia.org, wrote "A photograph of the Eiffel Tower in Paris, ...", and FLUX drew the real tower; 85 s for everything, most of it the description on the processor.

### Up to `e532c8a` (1.9.10): the fast model writes the description, and the pill names every model

- **Taken over, `fad00e7`:** with **Verbeteren met web** the fast model (Snel) writes the description: the one chosen under Modellen, else the smallest downloaded. The heavy model only when there is no other, or when it runs already and the fast one does not fit beside it. Pages are cut to 3,000 characters for the fast model's 16K context. The status pill names every model in memory with its role ("Ministral-3-8B actief (Snel) · FLUX.2 tekent", "Geen model geladen" when nothing is); the Engine card under Modellen keeps only its own engine. Beeld says who does what and lists every step with its model, and the list stays after the image. The speed gauge counts the second engine too.
- **Adapted:** the second engine runs on the processor here, so the fast model takes no room on the card and stays beside the image when the RAM has room for both; it stops only when it does not. The heavy model shares the card with FLUX and still always stops, as a step on the page. Measured here with Ministral as the fast model and DeepSeek chosen but not downloaded, as the owner had it on the Mac: Ministral loaded in 4 s, searched, read rijksmuseum.nl and one more page, wrote the description, stayed beside FLUX; 89 s for everything with a 512 x 512 image, most of it the description on the processor.
- **Does not apply:** `8998723` and `e532c8a` (the Mac's releases), `4fe3a48` (a sentence in the Mac's Dutch README).

### Up to `6696609` (1.9.8): a better description with the web, and one way to choose every role

- **Taken over, `c954667`:** **Verbeteren met web** on Beeld. The chosen chat model writes a better English description first, with web_search and fetch_url (at most four rounds, the last may only answer), and what goes to the search engine is cleaned as in the documents module. The chat model starts for it when it does not run. The page shows the description the image model got and the pages it came from; the user's words stay the image's description. When it fails, the image is made from the user's own words and the page says why. Measured here with Ministral and "het Rijksmuseum in Amsterdam bij avondlicht, foto": it described red brick and symmetrical towers, and FLUX drew a building like the museum, 18 s in all.
- **Not taken over:** a small chat model staying beside the image when the Mac has room. Here the chat model and FLUX share the card, so the chat engine still stops first.
- **Taken over, `6696609`:** every model card has role buttons in one style: Zwaar and Snel for a model that chats, Afbeeldingen for one that draws. Snel is now the user's choice (choosing it again goes back to the automatic one), and the heavy model is never the fast one too. Documents shows three rows alike: Zwaar (now with Start nu and Stop), Snel, and Afbeeldingen with Stop while an image is made. The buttons' names for screen readers now follow their text.
- **Does not apply:** `b8cbe33`, the Mac's own release.

### Up to `93853bc` (1.9.7): images among the models, and on the meters

Measured on the test PC (RTX 5060 Ti 16 GB): Compare makes the same image as on the Mac (a lighthouse, number 7, 1024 x 1024, with loading) in 10 s, about 5,000 image tokens per second. The Mac's figures (46 s, 450 per second on an M3 Max) are not measured here.

- **Taken over:** FLUX on the Models page with an "Afbeeldingen" badge and **Kies voor afbeeldingen**, chosen apart from the chat model; the page Beeld uses that choice. Compare lists FLUX and measures it by making that image, in charts of its own, with "–" in the text columns. The speed gauge, the mini dashboard and the token chart count image tokens (16 x 16 pixels, all of them in every step) while the model draws, and their scale grows with them. The page says that the model follows an English description best (`02e74f3`).
- **Not taken over:** working in parts only where memory is short. On the Mac that saves a third of the time; on a card here it costs about a second (8 s against 9 s at 1024 x 1024) and halves what the card holds, so the card keeps working in parts.
- **Does not apply:** the Mac's own commits for 1.9.7 and its README, the Mac's measurements, and `3060ea3`, which took Windows over to the Mac. Its note about Qwen3.8 is only for colleagues who kept the old files; the Windows catalog follows the new ones.

### `da25656` (1.9.6): a Mac with 16 GB can use Qwen3.6

All numbers here are computed by the planner for Qwen3.6-35B-A3B. None has been measured on a real PC with 16 GB of RAM.

- **Taken over:** when no model fits, the app recommends the smallest one that streams, if the dense weights, the buffers and a cache of 2 GB fit in VRAM + RAM. The system check says that it streams, has not been measured on so little memory and may be slow. The model card says so too.
- **Adapted, the 16K context:** on the Mac every PC below 24 GB gets it. Here it goes only to a PC that still reads from the SSD at 32K. A PC with 24 GB reports a little under 24 GiB, so the limit is 23 GiB.

| 16 GB RAM with | 32K context | 16K context |
|---|---|---|
| 16 GB card | nothing from the SSD; keeps 32K | one more layer's experts on the card |
| 12 GB card | cache 4.5 GB, 4.4 GB from the SSD | cache 4.8 GB, 4.2 GB from the SSD |
| 8 GB card | cache 0.2 GB, 8.7 GB from the SSD | cache 0.5 GB, 8.5 GB from the SSD |
| 6 GB card | 11 layers on the card | 15 layers on the card |
| No card | 6.1 GB of experts in RAM, 13.5 GB from the SSD | 6.4 GB in RAM, 13.3 GB from the SSD |

- **Not taken over, the expert cache limit of 3 GB:** on the Mac it protects memory that cannot be reclaimed. A separate card's VRAM is not system memory, so the limit would only send more to the SSD (12 GB card: 5.9 GB instead of 4.4 GB at 32K). Graphics in the processor are not used here, except on a Halo APU, which comes with far more memory.
- **Does not apply:** the wording of the label "too big" (there is no such label here), the smaller reserves on a small Mac (Windows keeps its fixed RAM margin, and there is no reserve for reading experts), the GPU limit row and the speed on 32 GB (neither exists here).

## Deliberate differences

- **GPU limit button:** removed, it has no Windows counterpart. The page file check takes its place.
- **Model files on disk** get a neutral name; the download servers keep theirs.
- **Moving models:** the Mac only points at another folder. Here the app also moves the files, resumable and verified.
- **Verwijder model** was the one added feature in the brief. The owner also asked for **skipping experts on GLM** (faster, answers change a little; off by default; see RESEARCH.md).
- **File and folder dialogs** use IFileOpenDialog directly: the WinRT folder picker failed with E_FAIL from this unpackaged app.
- **Stop for the main engine** closes its parent pipe rather than sending a console break, because the engine would otherwise lose its expert profile.
- **pwsh from the Microsoft Store** is skipped for `run_command`: its children leave the Job Object. Windows PowerShell 5.1 runs instead.

- **Ministral-3-8B Q4,** a small model for a PC with 8 or 16 GB of RAM, is in this catalog and not yet on the Mac. It has no experts, so the planner treats it differently here: it never repacks into RAM (the same 5.3 tok/s, 3.6 GB less private memory), has no expert cache, and gets 8K of context where memory is short. It is only recommended on a PC with less than 23 GiB and a card of at most 8 GB; a larger card still gets Qwen3.6.

- **Making images** is here and not yet on the Mac: the **Images** page, FLUX.2 Klein 4B and an image engine of its own, one process per image so that all its memory is free afterwards. See `ImagePlanner` for what was measured. The Mac has one pool of memory and another GPU backend, so its rules will differ.

## Mac gaps fixed here (brief §11)

| Mac behaviour | Here |
|---|---|
| A catalog address follows the newest version, so a file the maker replaces only fails once it has come down | Every address names one commit; the size is checked before a byte is written, and the model's download stops at once |
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
- **Images on other cards:** the image engine has only run on an RTX 5060 Ti (compute capability 12.0) and on the processor. RTX 20, 30 and 40 are built but not run, and Vulkan was only tried on that same NVIDIA card. A card that refuses falls back to the processor.

**Never run on real hardware**

- A clean Windows 11 PC, and any PC other than the one test PC.
- A PC with 8 or 16 GB of RAM: plans and recommendations are computed only. Ministral was measured on the test PC with limits on layers, threads and working set, which stand in for such a PC.
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
