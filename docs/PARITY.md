# Parity with MacAIMagic 1.14.1

How this version relates to MacAIMagic at commit `80f6c87`, the last Mac commit taken over. [CLAUDE.md](../CLAUDE.md) says how to take over newer ones. Test cases are listed one by one in [PARITY-tests.md](PARITY-tests.md).

## Swift files and their counterparts

| Swift | Windows | Status |
|---|---|---|
| `MacAIMagicApp`, `AppState` | `App/App.xaml.cs`, `App/AppState.cs`, `App/ShellWindow.cs` | Done |
| `Chat/ChatClient`, `ChatModels` | `Chat/ChatClient`, `ChatModels`, `ChatTexts`, `StreamThrottle` | Done, tested against real GLM |
| `Chat/WorkspaceTools` | `Chat/WorkspaceTools`, `CommandRunner`, `KillOnCloseJob` | Done, sandbox extended for Windows |
| `Chat/Skills`, `skills/*` | `Chat/Skills`, `Core/Resources/skills/*` | Done, Windows skills with the same names |
| `Chat/SystemTools` | `Chat/SystemTools` | Adapted: PowerShell allowlist, UAC, restore point, registry exports; admin steps not run with a real UAC prompt |
| `Chat/ModelGate`, `Chat/WorkSteps` | `Chat/ModelGate`, `Chat/WorkSteps` | Done; a queue per engine (base address), the folder per run as an AsyncLocal |
| `Chat/MCP/*` (`MCPClient`, `HTTPTransport`, `MCPOAuth`, `MCPManager`) | `Chat/Mcp/*` (`McpClient`, `HttpTransport`, `McpOAuth`, `McpManager`, `McpModels`) | Adapted: cmd /c for what is no .exe, Credential Manager instead of Keychain; tested on the Mac's fake servers, not against a real service |
| `Chat/MCP/CalendarConnector` | none | Not available: macOS calendar and reminders (EventKit) |
| `Chat/Tasks`, `TaskTool`, `TaskRunner` (Heartbeat) | `Chat/Tasks` (TaskSchedule, TaskStore, TaskTool, Heartbeat), `App/AppTasks` | Done; the timer catches up after sleep, no wake event |
| `Support/Notifier`, `Views/MenuBarView` | `App/Notifier` (Windows notifications), `App/TrayIcon` (notification area, Start with Windows) | Adapted |
| `Views/TasksView`, `TasksSettingsView`, `ConnectorsView`, `StepsCard` | `App/Views/TasksPage`, `SettingsDialog` (tab Taken), `ConnectorsTab`, `ChatPage` (steps) | Done; the connector editor and log open inside the tab (no second dialog over Instellingen) |
| `Support/Help`, `help/*` | `Core/Support/HelpCenter`, `App/Views/HelpPage`, `Core/Resources/help/*` | Done, topics written for Windows |
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
| `Chat/Telegram/*` (`TelegramBot`, `TelegramService`, `TelegramBridge`, `TelegramFormat`), `TelegramSettingsView` | `Chat/Telegram/*` (same names), `App/AppTelegram`, `App/Views/TelegramTab` | Done; token in the Credential Manager, accounts and offset in `telegram.json`; voice messages not understood (no recognizer for recordings) |
| `Chat/Agent/AgentTools`, `MailTools` | `Chat/Agent/AgentTools`, `MailTools` (`OutlookMail`) | Adapted: classic Outlook through COM instead of the Mail app; without it a draft goes through mailto: |
| `Chat/Agent/BrowserTools` | `Chat/Agent/BrowserTools`, `App/BrowserWindow` (WebView2) | Done; the page script is the Mac's, its arguments go in as JSON |
| `Chat/Agent/ComputerTools` | `Chat/Agent/ComputerTools` (UI Automation, SendInput) | Adapted: programs by .exe path, a Windows never-list, no Alt+F4 or Windows key |
| `Support/Voice`, `ModernSpeech`, `AgentSettingsView` | `App/Speech`, `App/Microphone`, `Engine/SpeechEngine`, `Chat/Agent/SpeechText`, `App/Views/AgentTab` | Adapted: reading aloud with Windows' voices; speaking and Telegram voice messages through the speech engine with Whisper large-v3-turbo instead of SpeechAnalyzer, both on the PC |

## Taken over since the first port

### Up to `80f6c87` (1.14.1): what the model prepares, and the chat's end

- **Taken over, `7d8f326`:** while the model writes a tool call (a whole program for write_file), the reply says what it prepares, from the name and the arguments so far ("Schrijft bestand demo.bas · 1.188 tekens"), updated on the first and every 25th chunk through the stream throttle. Those chunks count in the speed. A reply with nothing yet says "Bezig…" beside the spinner. Tests: ChatClientTests.APreparingToolCallSaysWhatComes (the Mac's cases) and WhileAFileIsWrittenTheReplySaysSo. The app's fake engine has a script `bestand` for it.
- **Already so here, checked:**
  - `80f6c87` (back in a conversation it shows the end): the chat page pins to the end on a new conversation and follows its size; in the Debug app a conversation of five answers was at 100% before and after Modellen.
  - `7c5ed51` (the last message above the step card and the approval card): the scroller follows its own size change while it is pinned to the end.
  - `344c57d` (a requested stop is never a failure): EngineBase.Terminated already counts any exit after a requested stop as stopped.
- **Not needed:** `2db95c9` (the Mac's release; WinAIMagic takes 1.14.1).

### Up to `3ece2c5` (1.14.0): a model in one click, conversations, the step list, skills in one file

- **Not needed:** `3ece2c5` (the Mac's release; WinAIMagic takes its number, 1.14.0), and in `c7cba76` the Mac's quick build without notarization (`NOTARIZE=0` in make_app.sh).
- **Taken over, `ab8f005`, the step list:** while the model works every step shows as it comes, the latest twelve with a "N eerdere stappen" button that opens the whole list; a finished answer folds into "N stappen". The rows stay on the left. (The owner had reported this on the Mac; the earlier Windows fix of that report was taken back then, on their word, and this is the Mac's version.)
- **Taken over, `2c4548e` and `b1ae116`, conversations:** "Nieuw gesprek" is the first row under Gesprekken; a trash can shows on hover unless the conversation is busy or waits; Delete deletes the conversation that has the focus; every way of deleting, the right-click menu too, asks first ("Dit gesprek verwijderen?"). Help topic Chat (nl, en).
- **Taken over, `20da6a0`, skills:** create_skill and the prompt line ask for one complete, condensed Markdown text without references to files in the work folder.
- **Adapted, `c7cba76`, a model in one click:**
  - Every downloaded chat model's card has "Start dit model" (another model stops first, through `EngineHost.Choose`), Stop while it runs, and "Laden…" while it loads. The Zwaar button is gone; Snel stays, and a model that was Snel stops being that when it is started.
  - The pill at the top is a menu: every downloaded model, Stop model, Modellen… and Live-dashboard. With nothing running it says "Geen model · klik om te starten"; "(Zwaar)" only when a fast model runs beside it.
  - The same words elsewhere: "Start <model>" and "Stop model" on Live, the chat banner, and "Gekozen model" for the card that was called Engine.
  - Windows: StartModel stops the fast copy of the same model through the existing StartEngineTask; the Mac also stops its fast engine. Here the memory planner already plans the second engine beside the main one, so that is left as it was.
  - Test: HostTests.StartingAModelStopsTheOtherOne.
- Checked in the Debug app with the fake engine and a sparse stand-in model file:
  - the version v1.14.0;
  - the new conversation row;
  - the delete question, cancelled;
  - the trash can on hover;
  - the menu's items;
  - "Start dit model" on the card;
  - "Gekozen model".
- Not checked: starting a real model from the card or the menu, and the step list during a long real task.

### Windows only, after `19213aa`: speech on the PC

- Speaking a question and Telegram voice messages now go to the speech engine (a third engine, beside the image engine, `wam-speech.exe` per backend) with Whisper large-v3-turbo (5 bit, 0.6 GB, in the catalog under `speech`, downloaded from the Agent tab or from the microphone's tip). Windows dictation is gone: it went through Microsoft's online service and its recognizer read no recordings. The Mac already recognizes on the device (SpeechAnalyzer), so this brings Windows to the same place.
- The microphone through winmm at 16 kHz, in memory; the end of a question is two seconds of quiet after at least 0.3 s of speech, against a floor that follows the room (`SpeechPause`). A stop by hand puts the text in the field without sending.
- Telegram voice messages (Ogg Opus) are decoded in C# (Concentus) straight to 16 kHz.
- Where it runs: on the card when 1.6 GB of it is free at that moment (a loaded model may fill it), else on the processor, and a card that refuses falls back to the processor. On the processor the model's window is cut to the recording plus 2 s.
- Measured on the test PC (RTX 5060 Ti 16 GB, i9-10900K), a Dutch question of 5.7 s spoken by Windows' voice Frank, through the C# code, word for word right apart from a comma: processor 13 s with the full window and 3 to 4.3 s with the cut one; Vulkan 1.4 to 2.4 s (19 s the first time, while the card builds its shaders); CUDA 1.2 to 2.3 s. Not measured: a real microphone, a card beside a loaded model, an 8 GB card or a PC without a card.

### Up to `19213aa` (1.12.0 and 1.13.0): Telegram, tables, Agent (mail, browser, programs, speech)

- **Not needed:** `75e03b6`, `fd7a8b6`, `bd471ee` (the Mac taking over Windows' 0.2.12 and the shared version number; WinAIMagic's `eccb3d8` already made it 1.11.0), `a638bd7` and `19213aa` (the Mac's releases).
- **Taken over, `9644cb0`, `e39624e`, `cdca20f`, Telegram:**
  - The same Bot API calls, long polling, pairing (a 6-digit code, 10 minutes, once, private chats only, strangers ignored and only their id logged), one conversation per account ("✈︎ Telegram · name"), /nieuw, /stop, /status, the growing answer (an edit per 1.5 s, split at 3500 characters, Telegram HTML with plain text as fallback, tables as columns in `<pre>`), photos and text files, approval cards (only the conversation's account, a late press refused, administrator rights never, a day without answer is no), and tasks and the heartbeat reporting to the first account.
  - Windows: the token in the Credential Manager (the connectors' store), the accounts and the offset in `telegram.json` in the app data folder. Messages are handled on the UI thread. With Telegram on, closing the window keeps the app in the notification area. A question from Telegram counts as the user's, so a task does not stop the model it loaded.
  - Found here: one request failing on a closed pooled connection put the status on "problem" for 5 s, and an approval card in that time was dropped. Cards and notifications now go while polling runs, and a request that fails on the connection is tried once more.
  - Tests with the Mac's names, on its own `fake_telegram.py` and a C# stand-in engine: TelegramFormatTests, TelegramAccessTests, TelegramBridgeTests (a stranger, pairing and the answer in HTML, /nieuw, the offset kept, the button, administrator rights, a voice message with a stand-in recognizer, tasks to the first account), plus VoiceIsOffSaysSo.
  - Not run: the real Telegram with a real bot, a real model through Telegram.
- **Taken over, `9761d8b`, tables in the chat:** a run of `| … |` lines is a grid with a bold header; not in code, not a pipe in a sentence (ChatTableTests). The settings gear of `cdca20f` was already here; the settings dialog is larger now (720 × 600) for its seven tabs.
- **Taken over, `79063d7`:** the done-word also goes at the end ("… KLAAR"), and the loading line names the chosen model's size from 50 GB on, else only the state.
- **Taken over, `8264a5d`:** a last sentence that waits for the user ("laat het me weten zodra …") ends the turn, also after tool use; a click on a text field puts the cursor there.
- **Adapted, `f5a167a`, `99c233e`, `02ed384`, Agent (Instellingen → Agent, all off by default):**
  - The Mac's run / ask / refuse per call and `Approval.EveryTime` (no "Altijd in dit gesprek" on the card or in Telegram for what asks every time).
  - **Mail** through classic Outlook's COM interface (all its accounts, inbox and sent mail, short numbers for Outlook's long ids), on an STA thread with a time limit. The new Outlook and the Mail app have no such interface; without classic Outlook mail_draft opens a mailto: draft and the rest says what is missing. Tests on a stand-in mailbox (MailToolsTests with the Mac's names; TheValuesGoToOutlookAsTheyAre replaces the AppleScript argv test). Outlook itself was not used: the owner's mail was not read.
  - **Browser** in a WinUI window with WebView2 and its own profile in the app data folder. The Mac's page script, rules (a link runs, a button asks, risky words ask every time, secret fields refused) and output. Tested on a real WebView2 in a hidden window with the Mac's test pages (OpenReadClickAndType, plus typed text that tries to break out of the script). Checked in the Debug app with the fake engine: the window "Browser van het model · Example Domain" opened and the model got the page with numbered links.
  - **Programs** through UI Automation: the allowed programs are .exe paths; the never-list holds command lines and script hosts, PowerShell, Terminal, the Registry Editor, Settings, Control Panel, Task Manager, MMC, password managers, the sign-in and UAC screens and WinAIMagic. Keys go with SendInput; Alt+F4 and the Windows key are refused. ComputerToolsTests with the Mac's names. Not run against a real program: Notepad might be the owner's open document.
  - **Speech:** reading aloud with Windows' own voices (the speaker under an answer, "Lees antwoorden vanzelf voor", a voice choice), on the PC. Dictation uses Windows dictation, which goes through Microsoft's online speech service and needs online speech recognition switched on in Windows; the SAPI recognizers on the test PC have no Dutch. Two seconds of silence send the question. A question while the model is not loaded loads it first and then goes (typed or spoken). Telegram voice messages are not understood: Windows' recognizer reads no recordings. VoiceTests.WhatIsReadAloud.
  - Not run: Outlook, a real website with a model, a real program, the microphone, a voice read aloud by ear.

### Up to `bcaeacd` (1.10.0 and 1.11.0): conversations at once, a step list, tasks and heartbeat, connectors

- **Taken over, `e807ec2`, conversations at once:**
  - Each conversation has its own run, approval card, stop button and loaded connectors (`ChatClient.IsBusy`, `ApprovalFor`, `Stop(id)`). The folder of a run and its conversation travel as `AsyncLocal` values, so two runs never use each other's folder (`WorkspaceTools.Current` was a plain static: the first run against two folders wrote into the wrong one, caught by the test).
  - `ModelGate` queues the conversations, first come first served, only while the model writes. Here there is one gate per engine address, so the second engine is not held up by the main one. Nothing yet sends a second conversation to the second engine.
  - The sidebar shows a ring for a conversation at work and an amber dot for one waiting for approval; its menu has Stoppen, and Verwijderen is grey while it runs. The chat says "Wacht op het model: het schrijft nu in 'X'".
  - Tests with the Mac's names: ModelGateTests (TurnsInOrder, AStopWhileWaitingLeavesTheQueue), ConcurrentChatTests (ATaskWaitingForApprovalDoesNotHoldUpAnotherChat, StoppingOneConversationLeavesTheOther). The Mac's `fake_engine.py` is a C# handler here (`SlowEngine`), with the same scripts and the count of requests at once.
- **Taken over, `85f0399`, the step list:** `task_list` with the same arguments, offered with a work folder, system management or a connector; the card above the input with a spinner at the step in progress, folding; a finished list goes with the next question. Tests WorkStepsTests.Parse and ConcurrentChatTests.TheModelsStepListIsKept.
- **Taken over, `2483a24`:** the done-word (KLAAR, DONE, FERTIG, TERMINÉ) is taken off the final answer; "Klaar!" in ordinary writing stays (test DoneWordIsNotShown). The editor checks of the connector form (a name, a command or an https address, a name not taken; arguments and environment are multi-line, so Enter is no save). The tests already had a conversations file of their own here (`Paths.AppData` per test).
- **Taken over, `1dbfeda`, `09aacfc`, `763b52d`, `cb2371a`, `1cf0094`, tasks and heartbeat:**
  - The same schedules and rules (once; daily or weekdays; weekly; every N hours within active hours, also past midnight; 12 hours to catch up; a single run switches itself off), with clock times in the PC's time zone. The Mac's TasksTests run here with their names (Europe/Amsterdam around 29 March and 25 October 2026), plus TaskToolTests, HeartbeatTests and a test of `schedule_task` through the card (TheModelPlansATaskThroughTheCard).
  - The planner looks every 30 seconds. There is no wake event here (the system events package is not in this app): after sleep the next tick comes within 30 s and a missed run is made up for then.
  - Each run is a conversation "⏱ title · date"; the model is loaded when needed and stopped again afterwards if a task loaded it and the user did not use it (Instellingen → Taken). During a run the PC does not sleep (`SetThreadExecutionState`, not the screen).
  - Notifications are Windows notifications (Windows App SDK); a click opens the conversation. A copy with `--data` registers none and shows a balloon on the tray icon instead.
  - The page **Taken** (between Beeld and Cluster, in the owner's order) with Nu uitvoeren, "Model laden…" while it loads, Bewerken, Verwijderen and Laatste uitvoeringen. The editor is a dialog.
  - The heartbeat as on the Mac (off by default, `HEARTBEAT.md` whose example counts as empty, NIETS leaves no conversation), with its settings in the tab **Taken**.
- **Adapted, `663afe1`, the background:** a tray icon instead of the menu bar (status, next task, the heartbeat, Open, Taken, Nu controleren, Alles stoppen, WinAIMagic afsluiten). With tasks or a heartbeat on, closing the window hides it and the model stops unless something runs; without, the app quits as before. **Opstarten met Windows** writes the user's Run key (off by default; `--background` starts straight in the tray); no test switches it.
- **Adapted, `04e001a`, connectors (MCP):**
  - `mcp.json` in the app data folder in Claude Desktop's format with the Mac's extra fields; `keychain:<name>` values come from the Windows Credential Manager (split over several credentials when a value is longer than one holds). Import from `%APPDATA%\Claude\claude_desktop_config.json` and `%USERPROFILE%\.claude.json`, switched off.
  - Stdio: a program found as an .exe starts directly, anything else (npx.cmd, uvx shims, a .bat) through `cmd /d /s /c`, in a Job Object so it stops with the app. Streamable HTTP with JSON or SSE, the session id and a DELETE at the end; OAuth as on the Mac with a listener on 127.0.0.1.
  - Loading on demand (`use_connector`), kept per conversation, the same tool names, rights, card with the arguments as JSON and the warnings.
  - Presets: Bestanden (npx), Web ophalen (uvx) and GitHub (a token). **Agenda en Herinneringen** is the Mac's own (EventKit) and is listed as not available on Windows.
  - Tests with the Mac's names on its own fake servers (`fake_mcp_stdio.py`, `fake_mcp_http.py`): HandshakeToolsAndCalls, StoppedProgramFailsInsteadOfHanging, JsonAndServerSentEvents, UnauthorizedWithoutOAuthSaysSo, OAuthLoginEndToEnd, ToolNamesAndDecisions, ConfigFileRoundTripInClaudeDesktopFormat, ManagerWithStdioConnector, ServerSentEventsParsing, PKCEAndMetadataAddresses. Added here: AProgramThatIsNoExeStartsThroughCmd, ArgumentsAreQuotedAsTheyAreReadBack, SecretsInTheCredentialManager (a real write, read and delete), ImportFromClaudeComesInSwitchedOff, and TheModelLoadsAConnectorAndAsksBeforeAChange (the chat loads the fake connector, runs the read-only tool without a card and asks for the destructive one).
  - Not run: a real online service, GitHub with a token, the npx and uvx presets (they download packages), a real model choosing a connector.
- **Taken over, `66cd607`:** a helper that answers again takes part again (Windows kept it marked); the reachability check now asks every chosen helper at once.
- **Checked in the app** (Debug build, own data folder, fake engine): the sidebar order; conversation A waited on its approval card while B got its answer, with the amber dot at A; the step list (1/3, a struck-through step); "maak een taak" gave the card "Taak plannen: Schijfoverzicht / Elke werkdag om 08:00" and the task on the Taken page with its next run; Nu uitvoeren made "⏱ Schijfoverzicht" under Laatste uitvoeringen; the Connectors tab added a connector to the fake stdio server, Test verbinding said "Werkt: 3 tools", and it was saved in `mcp.json`; the Taken tab; closing the window with a task on left the app running with the window hidden. The connector list first failed to come back after the editor (an element reused in the tree); fixed before this commit.
- **Not checked:** a Windows notification on screen and its click, the tray menu by hand, Start with Windows, a task starting on its own at its time, a heartbeat with a real model, connectors with a real model.
- **Not needed:** `ed32830` and `bcaeacd` (the Mac's releases), and the rest of `66cd607` (it took Windows over).

### Up to `2631fc9` (1.9.13 and the agent features): Controleer op update, skills, system management, help, Qwen3.6 Heretic

- **Taken over, `a09ad3c`, Controleer op update:**
  - A model that is here and of the catalog's version has **Controleer op update** on its card.
  - It reads no model file. It compares size and the sha256 in each .ok with this version's catalog, then sends HEAD requests (no redirect followed) for the pinned address and the same file on `main`.
  - An update of the catalog's files starts at once. Otherwise the card says "Geen update beschikbaar", or that the maker has newer files this version does not use yet, or (no answer) that the files match this version.
  - Tests:
    - the Mac's names: AnOldOkIsTrustedOnItsSize, TheMakersServer;
    - added here: TheMakerHasNewerFilesOrNot and ControleerOpUpdateReadsNoFile, with the model file locked against reading during the check.
  - TheMakersServer was run for real (WINAIMAGIC_NETWORK_TEST=1): Ministral None, Qwen3.8 None. The Mac expects MakerHasNewer for Qwen3.8 because it still pins the files from before 2026-11-04; this catalog took the new ones over earlier.
  - In the app (Debug build, own data folder, a hard link to the owner's Ministral), the button said "Geen update beschikbaar." after asking the real server.
- **Adapted, `8b578c1`, `e3c8c55`, `c882c76`, `2631fc9`, skills and system management:** the same design as the Mac (plan, approval, run, check, undo), with these Windows counterparts.
  - *Settings:* tab **Systeembeheer** with `Skills` and `SystemManagement`, both on by default. The rights are `RightSystemRead` (allow) and `RightSystemChange` (ask), with the warning for allow and a ? that opens the help. With both off, the model gets the same tools and system prompt as before (test SkillsGoInThePromptOnlyWhenOn).
  - *Skills:*
    - The same SKILL.md format; the eight built-in skills have the Mac's names, with `platform: windows`.
    - They ship embedded in the app (`src/WinAIMagic.Core/Resources/skills`). Your own go in `Skills` in the app data folder, and one with the same name replaces the built-in one.
    - Only names and descriptions go in the prompt, sorted; `use_skill` gives the text.
    - Every read command in the skills was run on this PC, read-only. `Get-Counter` fails on a Windows that is not in English (the counters have translated names), so diagnose reads the load with `Win32_PerfFormattedData_*` instead. `powercfg /requests` and `manage-bde -status` need administrator rights, and the skills say so.
  - *system_read:* one PowerShell command from an allowlist:
    - any `Get-` cmdlet except Get-Credential;
    - `powercfg` queries; `ipconfig` without `/release`/`/renew`;
    - `winget list|show|search`, and `upgrade` without a package;
    - `reg query`, `netsh … show`, `sc query`, and similar.
    - `$`, backtick, `@`, `;`, `|`, `&`, `<`, `>`, `(`, `)`, `{`, `}`, `[`, `]` outside quotes are refused. `$` and backtick are refused inside quotes too, since PowerShell expands them there.
    - A check written as `… 2>$null`, `2>nul`, `-ErrorAction SilentlyContinue` or `|| echo "…"` is read without that tail (`e3c8c55` in PowerShell form).
  - *system_change:*
    - Admin steps run together after **one UAC prompt**: the elevated PowerShell runs a script that writes each step's output and status to a log, because an elevated process gives no output back. No in the UAC prompt (error 1223) means nothing changed.
    - The Mac's bash script runs every command even after one fails. Here the script stops at the first failing step, so the steps after it really are not run (test AdminStepsShareOnePrompt, which runs the same script without UAC).
    - The card's checkbox makes a **restore point** first (`Checkpoint-Computer`, through Windows PowerShell 5.1 because PowerShell 7 does not have it). A missing restore point does not stop the plan, and the report says whether one was made. Windows makes at most one per 24 hours, and the card says so.
  - *Undo:*
    - Every registry key a user step writes (`reg add/delete`, `*-ItemProperty -Path`) is exported before the change.
    - On undo, a key that still differs is imported again. Values the change added are removed, because `reg import` only adds. Explorer is restarted when a step restarted it.
    - Test ApplyAndUndoRoundTrip, on `HKCU\Software\WinAIMagicTest`, with a deliberately wrong undo and an added value.
  - *Warnings, not blocks:*
    - firewall, Defender, SmartScreen, BitLocker, UAC, users, bcdedit;
    - permanent deletion, execution policy, hibernation off, a step that elevates itself, iex;
    - Windows services, proxy, hosts, Windows Update, restart;
    - and, as on the Mac, a plan after the web or a step without undo.
  - *The record:* `Changes\<id>\plan.json` in the Mac's JSON format.
  - *The rest as on the Mac:*
    - there is no list of changes in the app; undo goes only through `system_undo`, with "Terugzetten:" on each step of its card (`2631fc9`);
    - an admin card has no "Altijd" button;
    - only work in a folder needs the model's KLAAR to end (`c882c76`);
    - system tools do not count for WINAIMAGIC.md.
  - *Checked in the app* with two scripts added to the fake engine (Debug only), on the test key:
    - the card showed the step with "Nu:", and after Toestaan the value had changed and the change was recorded;
    - the admin card showed Beheerder, the restore point checkbox, no Altijd, and the UAC line. After Weigeren nothing changed.
  - *Not run:* a real UAC prompt with admin steps, a real restore point, and a plan from a real model. Those change this PC's settings, which the owner does themselves.
- **Adapted, help:**
  - A **Hulp** page in the sidebar, a ? in the title bar for the page on screen, F1, and a ? in the Systeembeheer tab.
  - 18 topics in `help/nl` and `help/en`, embedded in the app, written for Windows: VRAM + RAM + SSD, two engines, UAC and a restore point, the app data folder, Ctrl, no Time Machine. Front matter icons are Segoe Fluent names.
  - The Mac's search scoring and block parsing, and its test names: BothLanguagesHaveTheSameTopics, EveryPageHasItsTopic, Search, Blocks.
- **Adapted, `c882c76`, Qwen3.6-35B-A3B Heretic Q4:**
  - The same file at the same commit, with memory and dense from the Mac. `check_catalog.py` found it on the server, headers only.
  - It is marked `"recommend": false`. Here a model that streams is recommended by the smallest size, and Heretic is 1 GB smaller than Qwen3.6, so it would have become the recommendation on 8 to 16 GB cards. On the Mac the order kept the original.
  - Licence: the Qwen community licence of the model it comes from, shown before the download. The Mac's commit says Apache-2.0, after the maker's page.
  - Computed here: it fits in VRAM + RAM wherever Qwen3.6 does (8 GB card with 32 GB RAM). Not downloaded or run here.
- **Not needed:** `15c5e32` (the Mac's 1.9.13 release).

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

- **Nanbeige4.2-3B Q4** (2.7 GB, Apache 2.0, bartowski's GGUF pinned at one commit), a small model that thinks and is strong with tools, is in this catalog and not yet on the Mac. It is recommended on a PC with 6 to 12 GB of RAM, where Ministral streams; 16 GB keeps Ministral, which also reads pictures. It runs its 22 layers twice per token, each pass with a KV cache of its own: 181 KB per token in 16 bits, measured on the 5060 Ti. A model without experts now never gets less than 8K of context, also where it only just fits (Nanbeige on 12 GB came to 4K).

- **Making images** is here and not yet on the Mac: the **Images** page, FLUX.2 Klein 4B and an image engine of its own, one process per image so that all its memory is free afterwards. See `ImagePlanner` for what was measured. The Mac has one pool of memory and another GPU backend, so its rules will differ.

## Mac gaps fixed here (brief §11)

- **The app knows itself (new here, after 0.2.9; the Mac should take it over).** A model asked to make a skill wrote a file in the work folder and a line in WINAIMAGIC.md: it did not know where skills go, and could not write there. Now, under Settings → Systeembeheer → De app (on by default):
  - `app_help` reads the app's own help (a topic, a search, or the list);
  - `app_status` gives the app's state: version, PC, every model with its id, fit and download state, the roles, the engines, the settings it may change, the rights and the folders;
  - `app_action` does it: open a screen or help topic, download, pause, check for an update or delete a model, choose the heavy, fast or image model, start or stop the engine, change a setting, make an image, start a new conversation. A change asks on a card under the system-change right, and deleting a model always asks. What would stop the engine the conversation runs on (stopping it, a new heavy model, an image) waits until the answer is done. It may not change its own rights, the API keys, the server, the model folder or the cluster;
  - the cluster in both: `app_status` shows the role, the helpers found (name, chip, GPU memory, direct cable or network, reachable, in use), the transfer and any problem; `app_action` has find_helpers, add_helper, use_helper, cluster_role (helper stops the engine after the answer and starts the helper), allow_lan (always asks, with the no-password warning), start_helper and stop_helper. A Mac helper is listed like any other: both apps pin the second engine at the same commit (`ddf48f8`), so they speak the same RPC protocol; not tried with real machines;
  - before a start with helpers, each is asked on its port for 3 s; one that does not answer stays out of that start and the Cluster page and app_status say why (firewall, a Public network, helper not started) instead of the start waiting for minutes. While loading, the bytes sent are shown against about what goes to the helpers, and 90 s without a transfer is said. Tests: ClusterCheckTests (a real port on this PC, a fake helper, a fake byte counter). Not run with a real second PC;
  - `create_skill` writes `Skills\<name>\SKILL.md` after a card, and the system prompt says skills go nowhere else.
  - Run with the real Ministral in the Debug build, in its own data folder: "maak een skill c64-test ..." gave a card and the SKILL.md; "zet het denken uit" set Thinking to false after its card, once the setting names were in the tool's description (before that, Ministral asked back); "welk model raad je aan" read app_status and answered without downloading. A first version let Ministral offer a 95 GB download nobody asked for (declined is what a user would do; the test script approved it, and the partial file was removed): the prompt now says to never download, delete or switch a model on its own. Not run with a larger model.

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
