# Opdracht: bouw WinAIMagic, de Windows 11-versie van MacAIMagic

Je bent de hoofdontwikkelaar van **WinAIMagic**: een native Windows 11-app met exact dezelfde functionaliteit als
MacAIMagic 1.9.5. WinAIMagic draait enorme mixture-of-experts-taalmodellen volledig lokaal. De gebruiker kan chatten, het
model laten werken in een map, Word-documenten laten maken, modellen vergelijken en het model als lokale API gebruiken.

## 1. Bronnen en hoe je ze gebruikt

| Bron | Rol |
|---|---|
| [mfidder2026/MacAIMagic](https://github.com/mfidder2026/MacAIMagic), commit `9b3d166` (v1.9.5) | **De specificatie.** Lees de hele repo voordat je ontwerpt: `App/Sources`, `App/Tests`, `tools/`, `patches/`, `scripts/`, `docs/RESEARCH.md`, `RELEASE/README.md`. |
| [mw00/project-maya](https://github.com/mw00/project-maya), commit `3a47bbe` (v1.1.51), MIT | **De hoofdengine op Windows.** Lees `docs/WINDOWS.md`, `docs/SETTINGS.md`, `docs/SETUP.md`, `docs/MODELS.md`, `serve/`, `maya.py`, `tools/ds4/`, `tools/mm3/`. |
| llama.cpp, commit `ddf48f85c933c1f438212a02eb21e7b001d2d37d` + `patches/` uit MacAIMagic | **De tweede engine** voor wat de hoofdengine niet kan (§5). |

**Rangorde bij een tegenspraak:** de code en tests van MacAIMagic, dan deze opdracht als die expliciet afwijkt, dan de
README. De schermafbeeldingen in `docs/screenshots` zijn **verouderd**: ze tonen nog de oude naam, 3 zijbalkitems en een oude
mini-dashboard. Gebruik ze alleen voor kaartstijl, kleuren en sfeer.

Dit document vat samen waar het om gaat, maar vervangt de Swift-code niet. Getallen, teksten en randgevallen neem je
letterlijk uit de code. Wat hier niet staat maar wel in de code of tests zit, hoort er ook bij.

## 2. Vaste regels

1. **Functiepariteit.** Elk scherm, elke knop, elke instelling, elke regel en elke test van MacAIMagic heeft een
   Windows-tegenhanger. Afwijken mag alleen waar §4 of §11 het zegt, of na vraag aan de opdrachtgever. Houd
   `docs/PARITY.md` bij: per Swift-bestand en per testcase de status en de Windows-tegenhanger.
2. **De naam "Maya" is nooit zichtbaar voor de gebruiker**: niet in de UI, documentatie, bestandsnamen, procesnamen,
   logbestanden, API-antwoorden of schermafbeeldingen. De engine is een "lokale engine", de modellen heten gewoon
   DeepSeek, GLM en MiniMax. Uitzonderingen: download-URL's, metadata in GGUF-bestanden, interne `MAYA_*`-omgevingsvariabelen
   (gebruik waar het kan de `STRATA_*`-aliassen) en de verplichte MIT-tekst in `THIRD_PARTY_NOTICES.md` (§13).
   Maak een CI-test die de UI-teksten, logfilter, API-antwoorden en het installatiepakket hierop controleert.
3. **Streamen van de SSD is de kern van het product.** Een model dat niet in VRAM + RAM past, moet gewoon starten en wordt
   nooit "te groot" genoemd. Noem de verwachte snelheid. Een aanbeveling voor een model dat wel past mag; de grotere blijven
   beschikbaar.
4. **Meet voordat je een snelheid noemt.** Elk getal in UI en documentatie komt uit een meting op echte hardware, met
   vermelding van de hardware. De schattingen in §6 zijn alleen een startpunt en mogen nooit in het product verschijnen.
5. **Niets met de hand installeren.** Een schone Windows 11-pc heeft alleen de installer en een GPU-driver nodig. Geen
   Python, Visual Studio of CUDA Toolkit voor de gebruiker. De build controleert dat alle afhankelijkheden meekomen.
6. **Lokaal en privé.** Alleen de webzoekfunctie en modeldownloads gaan naar buiten. Geen telemetrie, geen updatecheck bij
   de upstream-projecten. Zet de updater van de engine uit en lever hem niet mee.
7. **Eerlijk rapporteren.** Falende tests en niet-geteste onderdelen meld je met de uitvoer erbij. Commit met duidelijke
   berichten, nooit force-push of geschiedenis herschrijven zonder toestemming.
8. **Taal.** Code, commits en de README in het Engels, plus `docs/README.nl.md` als Nederlandse samenvatting, zoals bij
   MacAIMagic.

## 3. Doelplatform

**Referentie-pc van de opdrachtgever:** Windows 11, één videokaart met **16 GB VRAM**, **128 GB RAM**, NVMe.

> Nog invullen door de opdrachtgever: GPU-type: `____` · CPU en geheugenkanalen: `____` · NVMe-type en vrije ruimte: `____`

**WinAIMagic is merkonafhankelijk en haalt het beste uit NVIDIA én AMD.** Hetzelfde installatiepakket werkt op beide. De app
herkent de kaart bij elke start en kiest zelf de snelste backend en instellingen. Zie §3a.

De app rekent net als MacAIMagic zelf uit wat past. Test het plan minstens op 8/32, 12/64, 16/128, 24/64, 24/192 en 48/256
(GB VRAM/RAM), elk als NVIDIA en als AMD, plus een Strix Halo-APU en een pc zonder bruikbare GPU.

### Waarom een Windows-versie geen vertaling is

Een Mac heeft één geheugenpool die de GPU rechtstreeks leest. Daar is de hele architectuur van MacAIMagic op gebouwd. Op
deze pc liggen 16 GB en 128 GB aan de andere kant van de PCIe-bus. **Deze onderdelen kun je dus niet overnemen en moet je opnieuw ontwerpen:**

- `MemoryPlanner` kent één pool (`physical`, `gpuLimit`). WinAIMagic kent drie lagen: VRAM, RAM en SSD.
- Patch `0002` schrijft experts direct in unified memory en gebruikt `posix_madvise`. Dat bestaat hier niet.
- De knop **Verhoog GPU-limiet** (`iogpu.wired_limit_mb`) heeft geen tegenhanger (§4).
- De metingen en getallen uit `docs/RESEARCH.md` gelden niet voor deze pc. De lessen wel: thinking-limiet, warme start, MTP
  alleen waar het meetbaar helpt, prompt-batchgrootte, en meten wat de SSD echt levert.

## 3a. De GPU-laag: één app, het beste per merk

### Regels

- **Eén abstractie.** Alle merkverschillen zitten achter `IGpuBackend` en in één gegevensbestand `gpu-profiles.json`. Buiten die laag
  komt geen `if nvidia` voor, ook niet in planner, UI of chat. Een CI-test zoekt hierop.
- **Geen merkvoorkeur in de UI.** Teksten zeggen "videokaart" en "VRAM". Merknamen staan alleen bij de hardware in Systeem.
- **Pariteit in functies, niet in instellingen.** Elk merk krijgt dezelfde functies, maar eigen, gemeten optimale instellingen.
  Kan iets op een merk echt niet, dan zegt de app dat en waarom, en biedt hij het beste alternatief.
- **Meten beslist.** De app kiest geen backend op aanname. De profielwaarden komen uit metingen, en **Kalibreer voor deze pc** stemt
  ze af op de echte machine.

### Herkenning en keuze

1. Tel de kaarten via DXGI (vendor-ID `0x10DE` NVIDIA, `0x1002` AMD, `0x8086` Intel), met naam, VRAM en driver.
2. Bepaal de capability: NVML/CUDA-compute capability, of de `gfx`-architectuur via HIP.
3. Slaat een backend niet aan (driver te oud, runtime laadt niet, testallocatie mislukt), probeer dan de volgende, en meld waarom.
4. Bij meerdere kaarten: de geïntegreerde GPU telt niet mee als doel, behalve op een Strix/Gorgon Halo-APU. Bij gemengde merken wint de
   backend met het meeste bruikbare VRAM; de gebruiker kan dit wijzigen onder Instellingen. Meerdere kaarten van één merk gebruikt de app samen zoals
   upstream dat kan (NVIDIA tot 16, AMD 2 getest).

| Kaart | Hoofdengine (VRAM + RAM + SSD) | Tweede engine (llama.cpp) | Status upstream op Windows |
|---|---|---|---|
| NVIDIA, compute capability ≥ 7.0 (V100, RTX 20 en nieuwer) | CUDA | CUDA | Experimenteel, maar door meerdere gebruikers gemeten |
| NVIDIA Pascal | CUDA, experimenteel | CUDA | Niet gevalideerd |
| AMD `gfx1100`, `gfx1201`, `gfx1151` (RX 7900, R9700 / RX 9070, Strix/Gorgon Halo) | HIP | HIP of Vulkan: meet welke het snelst is | Experimenteel; gemeten op APU's en RX 7900 XTX, **niet** op een 16 GB-kaart |
| AMD `gfx1030` (RX 6800/6900) | HIP, experimenteel; geen matrix cores, dus trager | HIP of Vulkan | Niet gevalideerd op Windows |
| Andere AMD-kaarten (o.a. RX 7800 XT, naar verwachting `gfx1101`: verifieer) | Niet beschikbaar | Vulkan | Beperkte modus, meld dit vooraf |
| Intel Arc en overige | Niet beschikbaar | Vulkan, buiten scope voor optimalisatie | Mag niet crashen |
| Geen bruikbare GPU | Niet beschikbaar | CPU | Traag, maar werkt |

Controleer de `gfx`-architectuur van elke AMD-16 GB-kaart vóór een belofte. De RX 9070-familie staat in de documentatie van upstream; de RX 7900
GRE is naar verwachting `gfx1100`, maar dat is niet gecontroleerd.

**In de beperkte modus** blijft de hele app werken: chat, documenten, werkmap, vergelijken, API. Alleen de snelheid en de grootte van de modellen
verschillen. De modelkaart zegt wat er verandert.

### Wat `IGpuBackend` regelt

| Onderdeel | NVIDIA | AMD |
|---|---|---|
| Engine-map | `engine\cuda\` | `engine\hip\` (`gfx1100;gfx1201;gfx1151`, los `gfx1030`), plus `engine\vulkan\` |
| Bouwen in CI | MSVC + CUDA Toolkit, `-DSTRATA_ENABLE_CUDA=ON`, alle `sm_*` | ROCm/HIP-clang met Ninja in de VS-omgeving (`tools\hip\build_maya_windows.bat`), `--backend hip` |
| Runtime naast de exe | `cublas`-DLL's | `amdhip64_7.dll`, `amd_comgr*.dll`, `rocm_kpack.dll`, hipBLAS. **Naast de exe**, anders laadt Windows de versie uit System32 |
| Apparaat kiezen | `CUDA_DEVICE_ORDER=PCI_BUS_ID`, `CUDA_VISIBLE_DEVICES` | `HIP_VISIBLE_DEVICES`, nummering volgens hipInfo |
| Basis-telemetrie | PDH `GPU Engine`, `GPU Adapter Memory` | Idem: dit werkt voor elk merk |
| Extra telemetrie | NVML in een apart kindproces: temperatuur, vermogen, PCIe | ADLX waar beschikbaar (eigen kennis, verifieer); anders tonen we die velden niet. Upstream leest op Windows alleen DXGI + PDH |
| VRAM-reserve (start) | 700 MiB, 1500 als de kaart het bureaublad aanstuurt | 3 GiB (upstream voor losse kaart én APU) |
| RAM-marge (start) | 6 GB | 16 GiB bij een losse kaart |
| Prompt lezen | `prefill auto` | `MAYA_GLM_PREFILL_SUB=1024` en `MAYA_GLM_PREFILL_MB=4096` bij ≥20 GB. Meet of 16 GB hier ook baat bij heeft. `MAYA_HIPBLASLT_TUNING` waar een tabel voor de kaart en versie bestaat; maak anders een tabel met `tools/hip/tune_hipblaslt` |
| Windows-specifiek | Standaard wisselbestand-eis | Kernel-flush (`GPU_FLUSH_ON_EXECUTION`, `MAYA_MM3_WIN_SUBMIT`). Lees de Windows-sectie van `docs/AMD_MAYA.md` |
| APU | n.v.t. | Wisselbestand ≥ GPU-aandeel + max(8 GB, 10% RAM); advies voor Variable Graphics Memory. VRAM en RAM zijn dezelfde chips: plan volgens upstream |

Alle getallen in deze tabel zijn startwaarden van upstream. Ze komen in `gpu-profiles.json`, sleutel `merk/architectuur/VRAM-klasse`, en worden vervangen door
gemeten waarden.

**Ook per merk meten en vastleggen:** welke modelformaten en vision op die backend werken (upstream: GLM en DeepSeek lezen afbeeldingen ook op AMD; controleer MiniMax en
de Qwen-paden op Windows), en of drafting (MTP, DSpark) daar helpt.

### Testen zonder beide kaarten

- Unit-tests draaien met een `FakeGpuBackend` voor elk profiel uit de bovenstaande tabel.
- Echte hardware: de opdrachtgever heeft **één** kaart. Het andere merk blijft "experimenteel, niet door ons gemeten" in de README totdat er een meting op echte hardware is.
  Stel een goedkope testroute voor: tijdelijke huur of leen van een kaart van het andere merk, en geef aan wat dat oplevert.
- **Exporteer meting**-knop bij Vergelijken: één bestand met hardware, profiel en resultaten, zonder persoonlijke gegevens. Niets gaat vanzelf naar buiten. Zo kunnen gebruikers van het andere merk resultaten aanleveren.

## 4. Architectuur (beslissingen)

### App

- **C# op .NET (huidige LTS) met WinUI 3 / Windows App SDK**, self-contained uitgeleverd. Mica/Acrylic voor de glasachtige
  kaarten, Win2D of LiveCharts2 voor grafieken. MVVM, één `AppState` als in MacAIMagic.
- **UI-thread-regel:** tekst uit een stream gaat nooit synchroon naar de UI. Maximaal elke 0,1 s bijwerken. Dit veroorzaakte
  in de Mac-versie een vastloper (1.9.4); er is een test die dit afdekt, port die mee.
- **Eén instantie** via een named mutex: een tweede start activeert het bestaande venster. Het sluiten van het laatste venster
  sluit de app.
- **Inactief verbruik onder 1% CPU.** Animaties alleen als er echt tokens lopen.

### Engine-laag

Eén interface `IEngine` (`Start`, `Stop`, `StopAndWait`, status, voortgang, log, telemetrie, capabilities) met twee
implementaties:

```
WinUI-app
 ├─ TieredEngine ─ gebundelde CPython ─ serve-laag (fork) ─ engine-exe's        (VRAM + RAM + SSD)
 └─ LlamaEngine  ─ llama-server.exe (CUDA, HIP of Vulkan, met geporte patches)  (kleine modellen, cluster, beperkte modus)
```

**TieredEngine.** De engines van Project Maya hebben **geen HTTP-server**. Ze spreken een regelprotocol met token-ID's over
stdin/stdout (`READY`, `GEN`, `T`, `PP`, `STAT`, `VRAM`, `INFO`, `DONE`, `STOP`, `QUIT`). De API's, tokenizers, chat-templates,
tool-parsers en metrics zitten in de Python-laag `serve/`. Lever daarom een **embedded CPython** mee, onzichtbaar in de
installatiemap, met alleen de benodigde pakketten (`jinja2`, `regex`, `numpy`, `gguf`, optioneel `Pillow`, `psutil`). Start
met `python -m serve.server --engine <e> --config <x>.json --port <p> --parent-pipe`. Een native port van `serve/` is
expliciet buiten scope voor 1.0.

**Vendor de engine als fork** in `engine/tiered` op commit `3a47bbe`. Volg upstream niet automatisch: het project bracht in
een maand 87 versies uit, Windows heet er experimenteel en CI bouwt geen Windows. Neem upstream-fixes alleen bewust over. In
de fork:

- hernoem de exe's (bijvoorbeeld `wam-glm.exe`, `wam-ds4.exe`, `wam-mm3.exe`, `wam-vision.exe`), de log-prefixes, `model_label`,
  `build_info` en configmappen;
- verwijder het updater- en dashboardpad uit het pakket (`serve/update.py`, `serve/web`);
- pin llama.cpp voor deze build op `3cf0325` (de commit uit de `setup.py` van upstream; apart van de `ddf48f8` van LlamaEngine);
- bouw per backend voor alle ondersteunde architecturen (CUDA-default is alleen `sm_120`; zie §3a), controleer of de
  multi-architectuurbuild op Windows werkt, en zet `-DSTRATA_PORTABLE` aan (AVX2-basis, statische CRT);
- patch de thinking-limiet (§5).

**Bouwen gebeurt in onze CI, nooit op de pc van de gebruiker.** Upstream bouwt tijdens de installatie (20-40 minuten met VS +
CUDA Toolkit of ROCm). Wij leveren binaries voor elke backend in één installer. `cudart` is statisch; de runtime-DLL's komen naast de exe (§3a).
**Controleer de actuele voorwaarden van NVIDIA én AMD** op toestemming voor het meeleveren en meld het resultaat. Is het pakket te groot, dan installeert de
installer alleen de backends voor de aanwezige kaart en haalt de app de rest op bij een kaartwissel.

**Packs** (ca. 1 GB, per model) bouw je op de pc van de gebruiker na de download, met de gebundelde Python en
`tools/iq_pack.py`, `tools/ds4/pack_gguf.py`, `tools/mm3/pack_gguf.py`. Toon voortgang. De `sources.json` bevat `mtime_ns`, dus
vooraf gebouwde packs zijn vermoedelijk niet overdraagbaar.

### Windows-tegenhangers

| macOS | Windows |
|---|---|
| Application Support `/MacAIMagic/` | `%LOCALAPPDATA%\WinAIMagic\` met `conversations.json`, `benchmarks.json`, `engine.log`, `engine-fast.log`, `WINAIMAGIC.md`, `models\` |
| `~/Documents/MacAIMagic/` | `Documenten\WinAIMagic\` (Known Folder API, ook bij OneDrive-omleiding) |
| `UserDefaults`, `@AppStorage` | `settings.json` in de appdata-map, atomair geschreven. API-sleutels met DPAPI versleuteld |
| `/bin/zsh -lc` | PowerShell: `pwsh` als aanwezig, anders Windows PowerShell 5.1, met `-NoLogo -NoProfile`, UTF-8-uitvoer (§8) |
| SIGINT/SIGTERM | Eigen process group met `CTRL_BREAK_EVENT` of het stop-commando van de engine. **Controleer dat de engine dan zijn expert-profiel/cache bewaart.** Daarna `TerminateProcess`, zelfde wachttijden als de Mac |
| Wees-engines (`ps`, ppid 1) | Elk kindproces in een **Job Object** met kill-on-close, `--parent-pipe`, plus opruimen bij start op basis van het exe-pad |
| `IOPMAssertion` | `SetThreadExecutionState(ES_CONTINUOUS \| ES_SYSTEM_REQUIRED)`, zolang een engine of helper leeft. Scherm mag uit |
| Quarantaine / `xattr` | Mark of the Web verwijderen van eigen binaries. Controleer ook of Defender binaries blokkeert en meld dat met een knop |
| Notarisatie | Authenticode-ondertekening (§12) |
| `sysctl iogpu.wired_limit_mb` | Bestaat niet. Vervangen door de wisselbestand-controle en VRAM-reserve (§7) |
| Metal `recommendedMaxWorkingSetSize` | `IGpuBackend` (§3a): DXGI voor iedereen, NVML of HIP voor details. NVML in een apart proces: een crash daar haalde bij upstream de server onderuit (#136) |
| IOKit GPU %, schijf | `IGpuBackend` (§3a); PDH `PhysicalDisk\Disk Read Bytes/sec`. Alleen de schijf met de modelmap én het totaal |
| `host_statistics64` | `GetSystemTimes`, `GlobalMemoryStatusEx`, `GetPerformanceInfo`, en de gepinde laag uit de engine-telemetrie |
| PDFKit `string` | PdfPig: alleen ingebedde tekst, geen OCR, een scan wordt geweigerd zoals op de Mac |
| `COMPRESSION_ZLIB` | `DeflateStream` (raw deflate) |
| `CGImageSource` | WIC of `BitmapDecoder`: pixelmaat en DPI |
| `NSImage`, JPEG | WIC: langste zijde max. 1344, nooit vergroten, op wit vlak, JPEG-kwaliteit 0,85 |
| `FileManager.trashItem` | Prullenbak via `IFileOperation` |
| `localizedStandardCompare` | `StrCmpLogicalW` |
| `NSOpenPanel` | `IFileOpenDialog`, `FolderPicker` |
| Toon in Finder | Toon in Verkenner (`explorer /select,`) |
| `NetService` (Bonjour) | `Windows.Networking.ServiceDiscovery.Dnssd`, met dezelfde servicenaam en TXT-velden |
| ⌘N, ⌘V, ⌘↩ | Ctrl+N, Ctrl+V, Ctrl+Enter |
| SF Symbols, SF Rounded | Segoe Fluent Icons, Segoe UI Variable |

Tekenlimieten in Swift tellen grafeemclusters, .NET telt UTF-16. Voor selecties en reeksen in de editor is dat juist
handig (de Mac-code rekent ook in UTF-16). Bij inkortlimieten mag een surrogaatpaar nooit doormidden.

## 5. Engines en modellen

### Modelcatalogus

Neem het schema van `tools/models.json` over en voeg per model `engine`, `pack` en de tier-gegevens toe. Elke download blijft
vastgepind op revisie en sha256. De volgorde is de aanbevelingsvolgorde.

| Mac-id | Op Windows | Engine | Status |
|---|---|---|---|
| `deepseek-v4.1-flash-compact` (277 GB) | Dezelfde bestanden; de sha256's staan ook in `tools/ds4/download_gguf.json` van upstream. Upstream heeft deze quant bijgewerkt van v1 naar v2 (ca. 12% dichter bij het origineel, 20% sneller). **Stel vast welke de Mac gebruikt en neem v2.** | Tiered (DeepSeek) | Ondersteund |
| `deepseek-v4.1-flash-q4` (460 GB) | Dezelfde unsloth-bestanden via `--ds4-quant gguf-q4` (upstream houdt die optie verborgen). Shard 8 is bij de Mac een gestempelde Q6_K-tabel: controleer dat de engine deze leest | Tiered | Grotendeels SSD; verifiëren |
| `glm-5.3-flash-compact` (109 GB) | Dezelfde unsloth-bestanden via `--gguf-dir` (upstream: experimenteel, ongemeten) | Tiered | Verifiëren |
| `glm-5.3-flash-q4` (200 GB) | Idem. Controleer dat elk expert-formaat op het GPU-pad draait | Tiered | Verifiëren |
| `qwen3.8-flash-next-q4` (116 GB) | Eerst via het pack (`qwen4exp` wordt door `iq_pack.py` geaccepteerd, maar is voor deze basisversie ongetest). Werkt dat niet: LlamaEngine | Tiered of Llama | Spike in fase 0 |
| `qwen3.6-35b-a3b-q4` (23 GB) | De architectuur is `qwen35moe`; de packer van upstream weigert die (gecontroleerd) | **Llama** | Ondersteund |

**Toevoegen voor kaarten tot 24 GB:**

| Nieuw (eigen id en naam) | Grootte | Waarom |
|---|---|---|
| GLM-5.3-Flash Compact 24 (upstream: `glm-maya-s24`) | 94,7 GB | 4-bit attention laat ca. 1,5 GB VRAM extra over voor experts, ca. 14% sneller schrijven |
| MiniMax-M3.1-Flash Compact (upstream: `minimax-maya-xs`) | 93 GB | Past in VRAM + RAM. Let op de eigen licentie (§13) |

**Niet opnemen:** de DFlash2-drafter (niet-commercieel, op één GPU vaak trager), `CYBER-FROST` (derde partij, vaste
systeemprompt, denkt altijd), abliterated- en "Derisked"-varianten.

### Verplichte aanpassingen aan de serve-laag

De Mac-client is geschreven tegen `llama-server`. Deze verschillen zijn gecontroleerd in de broncode:

| Onderwerp | Verschil | Wat je doet |
|---|---|---|
| **Thinking-limiet** | `thinking_budget_tokens` en `reasoning_budget_message` komen nergens voor in de serve-laag; het budget is een instelling voor de hele server | **Patch de fork** zodat beide per verzoek werken, ook budget 0. Dit is essentieel: zonder limiet schreef GLM in een test nooit code (`docs/RESEARCH.md` §6). De sluittekst moet in het gesprek komen zoals bij llama-server |
| `enable_thinking:false` | Werkt op GLM | Stuur budget 0 toch mee: één clientpad voor beide engines |
| Reasoning | `reasoning_content`-delta's komen overeen | Geen aanpassing |
| `timings`, `usage` | Zelfde namen, op de laatste chunk | Geen aanpassing; test `predicted_per_second`, `prompt_per_second`, `total_tokens` |
| `/slots` | Heeft alleen `{id,n_ctx,is_processing}`, geen `n_decoded` | Snelheid uit `/metrics` (`live.tok_s`, **JSON**, niet Prometheus); `is_processing` blijft de "engine bezig"-melding |
| Te lange context | 400 tenzij `fit_max_tokens` | Controleer dat de client hem herkent (Mac: code 400 + "context"), anders werkt de samenvat-retry niet |
| Modelwissel | `/models/load` kent alleen het lopende model | De app stopt en start de engine zelf |
| API-sleutel | `api_key` in de config | Nooit op de commandoregel, ook niet bij LlamaEngine als een alternatief bestaat |
| Lusbewaking | De server grijpt zelf in bij herhaalde tool-calls (`serve/loop_guard.py`) | Laat aan, maar test samen met de doorwerk-regels (§8) en log wanneer hij ingrijpt |
| Namen | `/props.build_info`, `model_label`, `model_name`, logregels | Eigen namen. `--alias` is het model-id |

Test alle requestvelden en het einde van de stream tegen **beide** engines, met dezelfde contracttest. `prompt_progress` is
alleen aanwezig met `return_progress:true`; bepaal de vorm van keep-alives tijdens lange prompts en zorg dat de client ze
overleeft.

### LlamaEngine

Bouw `llama-server`, `llama-bench`, `llama-perplexity` en `ggml-rpc-server` op de gepinde commit, per backend (CUDA, HIP, Vulkan, CPU) en met RPC, zonder
externe DLL's behalve wat meekomt. Port de patches:

- `0001` (Engram-splitsing, `engram.*`-sleutels, `LLAMA_MMAP_PREFETCH`) is platformonafhankelijk.
- `0002`: vervang `posix_madvise(WILLNEED)` door `PrefetchVirtualMemory`, en meet ook gewone overlapped reads. Directe parallelle
  schrijfacties zijn alleen voor apparaten die hostgeheugen mappen, dus op een losse kaart alleen parallel bronnen lezen. Controleer of `--moe-cache-mib` op elke backend werkt, Vulkan in het bijzonder. Herhaal de meting
  uit `docs/RESEARCH.md` §2 op Windows en kies op basis daarvan.

De startcommando's en omgevingsvariabelen staan in `EngineController.swift`. `--n-cpu-moe` en `--moe-cache-mib` berekent nu het
nieuwe plan uit VRAM.

### Hoofdengine starten (upstream-opties, verifieer ze per commit)

```text
GLM:      --serve --glm-pack <pack> --max-context N --expert-cache auto --prefill auto --kv-streaming on
          --expert-profile <bin> [--vram-reserve-mib 1500] [--kv int8]
DeepSeek: --serve --pack <dir> --max-context N --tp 1 --threads on --dspark none [--vision off]
          [--reserve-mib 1500] [--mmap-experts auto|off] [--cpu-threads N]
MiniMax:  --serve --pack <dir> --max-context N --profile <bin> --kv-streaming on --dspark off --vision on
          [--reserve-gb 1.5] [--kv-cache int8]
```

Config-JSON: `exe, args, cwd, tokenizer, model_name, gpu, sampling, reasoning_effort, lib_dirs, port, log, env, vision`, plus
`host, api_key, thinking_budget, idle_unload_s, fit_max_tokens, model_management`.

De vlaggen hierboven gelden voor beide merken; het merkverschil zit in de omgeving en het profiel (§3a). Probeer op NVIDIA voor GLM ook `MAYA_GLM_RAM_RESIDENT=1` met `MAYA_GLM_PROMOTE_MIN=6` (upstream: 1,9× op een 4090 D met 128 GB)
en `MAYA_GLM_RAM_BALANCED=1`. Neem alleen op als het hier meetbaar helpt. Voeg de kalibratie van upstream (`tools/calibrate*.py`,
10-15 minuten) toe als knop **Kalibreer voor deze pc** op de modelkaart. De resultaten komen in de `env`/`args` van de config.

## 6. Plan en aanbeveling

Nieuwe `MemoryPlanner`, zelfde vorm als de Mac-versie (pure functie, volledig getest), met als invoer: VRAM totaal/vrij, RAM
totaal/beschikbaar, wisselbestand, modelgegevens uit `GGUFIndex`, context, vision, helpers.

Uitkomst per model, altijd zichtbaar op de modelkaart en in de planbalk:

| Label | Betekenis |
|---|---|
| **Past in VRAM + RAM** (mint) | Alles wat per token nodig is, zit in VRAM en RAM. DeepSeek's Engram-tabellen blijven op SSD |
| **Streamt van SSD** (amber) | Een deel van de experts komt van de SSD; toon de **gemeten** snelheid |

Geen derde label "te groot". Blokkeer alleen als een harde minimumeis ontbreekt, en zeg dan precies welke. Het minimale RAM-
en VRAM-gebruik per engine leid je af uit upstream en verifieer je.

**De engine verdeelt de experts zelf over de lagen** (`--expert-cache auto`). De planner van de app voorspelt, de
engine beslist. Toon na het starten de werkelijke verdeling uit de telemetrie. Wijken die sterk af, werk dan de voorspelling bij.

**Context en vision** volgen de Mac-regels: `min(instelling, GGUF context_length, ruimte)`; vision uit als hij het model uit het
geheugen duwt, met de drempel 16 384 tokens. KV-kosten per architectuur staan in `GGUFIndex.swift` en `maya.py`. Doelwaarden voor 16/128
volgens de formules van upstream (controleer ze): DeepSeek 256K, MiniMax XS 128K, Qwen 128K, GLM 64-128K.

**Aanbeveling** per pc: het eerste model in de catalogus dat past. Voor 16/128 is dat volgens de onderstaande schatting
GLM-5.3-Flash Compact 24; bevestig met meting in fase 0.

### Startpunt voor metingen op 16 GB + 128 GB

**Dit zijn schattingen op basis van andere hardware, geen beloftes.** Er is geen enkele meting bekend op één 16 GB-consumentenkaart onder
Windows.

| Model | Past? | Vergelijkbare meting (upstream) | Schatting |
|---|---|---|---|
| GLM-5.3 Compact 24 (94,7 GB) | Ja | RTX 5070 12 GB, 126 GB, EPYC: 16,6 tok/s, prefill 323 | 17-24 tok/s |
| DeepSeek Compact (121 GB in geheugen, 161 GB tabellen op SSD) | Ja | RTX 4000 Ada 20 GB, Win11, 255 GB: 21,1 tok/s, prefill 518 | 13-20 tok/s |
| MiniMax Compact (93 GB) | Ja | Geen meting op kleine kaart | 7-14 tok/s, onzeker |
| GLM-5.3 groot (156 GB) | Ca. 20 GB op SSD | RTX 4090, Win11, 192 GB: 16,4 tok/s | 8-13 tok/s |
| DeepSeek Q4 (460 GB) | Grotendeels SSD | Niet vergelijkbaar | Meten |
| Qwen3.6 (23 GB) via llama.cpp | Ja, deels op CPU | Geen | Meten |

De metingen hierboven zijn NVIDIA. Op AMD is er geen vergelijkbare 16 GB-meting; de dichtstbijzijnde (grotere kaarten) zijn: R9700 32 GB met 128 GB RAM, GLM-5.3 quant S: 21,4 tok/s, en RX 7900 XTX 24 GB met 63 GB RAM,
DeepSeek Compact: 18,4 tok/s, prefill 395. Op AMD blijft bovendien ongeveer 1,5-2,3 GiB minder VRAM over voor experts, door de ruimere startreserve. Meet of die reserve op een 16 GB-kaart omlaag kan.

CPU en geheugenkanalen wegen zwaar mee, omdat veel experts op de CPU draaien. De NVMe bepaalt de rest.

## 7. Onderdelen die op Windows anders uitpakken

### Wisselbestand, VRAM-reserve en vastgepind RAM

Dit zijn de Windows-valkuilen die de snelheid bepalen. Ze horen in het plan én in de systeemcontrole:

- **Wisselbestand:** VRAM en de gepinde laag tellen allebei mee voor commit. "Door systeem beheerd" kan op een 128 GB-pc op
  8 GB blijven staan, en dan pint de engine te weinig. Upstream mat op een RTX 5090: 2,4-4,1 → 8,4-11 tok/s. Eis: **minstens VRAM + 9 GB**,
  dus 24 GB hier (de eis op een APU staat in §3a). De knop **Stel in** vraagt via UAC om toestemming en meldt dat een herstart nodig is.
- **VRAM-reserve:** een volle kaart faalt op Windows niet, WDDM pagineert. De snelheid zakt dan naar enkele tokens per seconde.
  Vrije-VRAM-metingen kunnen ca. 1 GB te hoog zijn. Instelling **VRAM-reserve** (standaard uit het profiel, §3a), en waarschuw als de engine `LOW` logt.
- **RAM:** de engine pint alles behalve ca. 6 GB en de start duurt enkele minuten. Toon dat vóór het starten, en
  bied aan om het model na inactiviteit te ontladen (`idle_unload_s`).
- **Niet in WSL2.** Antivirus kan bestanden vasthouden tijdens hernoemen: probeer opnieuw.
- **Upstream-documentatie:** `docs/WINDOWS.md`.

De knop **Verhoog GPU-limiet** verdwijnt. Op die plekken (Modellen, Systeem, Cluster) komt de wisselbestand-knop.

### Twee modellen tegelijk in Documenten

Op de Mac draait in Documenten een snel model (poort + 1, kleinste model, 16K context, geen vision) naast het zware, als
`resident + klein + 2,9e9 + 8e9 ≤ RAM`. Op deze pc pakt de zware engine bijna alle VRAM en RAM. Ontwerp dit opnieuw:

1. Start het zware model met een lagere RAM-laag (`MAYA_GLM_RAM_HEADROOM_GB` e.d.) en meer VRAM-reserve als een snel model
   gepland is. Een zware engine die daarvoor moet herstarten, doet dat zichtbaar.
2. Draai het snelle model (Qwen3.6 via LlamaEngine) op de CPU of met een klein aantal GPU-lagen. **Meet het.**
3. Helpt het niet, dan doet één model alles: die terugval bestaat al. Alle overige regels uit `DocumentModels.swift`
   (statusregel, knop Start/Stop/Probeer opnieuw, geen herhaalde pogingen na een mislukking) blijven gelijk.

### Live dashboard

Alles van de Mac-pagina, plus wat een drielaagse pc nodig heeft:

- De grafiek "Geheugen" krijgt **VRAM** (gebruikt/totaal) en **RAM** (gebruikt, gepind).
- Nieuwe kleine statistieken: expert-trefkans in VRAM, verdeling VRAM/RAM/SSD, GPU-temperatuur.
- `disk_mb` uit upstream is alleen Linux: gebruik PDH.
- Bemonstering: 0,5 s actief, 2 s rust, 90 s venster, zoals de Mac. Gebruik het tokentempo uit de engine, zodat de meter ook werkt
  als een ander programma de API gebruikt.
- Gauge: maximum 30 op de pagina, 60 in de zijbalk.

### Cluster

De hoofdengine heeft **geen** netwerkclustering. Het cluster werkt dus via `ggml-rpc-server` en LlamaEngine, zoals op de Mac:

- Zelfde rollen, schermen, handmatige invoer en lagenverdeling (`--tensor-split`, gehele lagen, helpers eerst). Poort 50052, servicenaam
  `_mdsrpc._tcp`, TXT `ip, chip, mem, gpu`. Op een helper is het budget de VRAM.
- Bij een model op TieredEngine zegt het cluster-scherm dat duidelijk en biedt het de modellen aan die wel kunnen.
- Verbinding: USB4/Thunderbolt-netwerk of ethernet. De helper luistert standaard alleen op een directe verbinding; LAN alleen na
  de expliciete schakelaar, met de permanente waarschuwing dat RPC geen wachtwoord heeft.
- **Test zoals op de Mac:** een helper op dezelfde pc (`127.0.0.1`). Er is maar één pc beschikbaar; meld dat echte hardware
  ongetest is. Mengen met Mac-helpers is buiten scope.

## 8. Functionele specificatie: chat

Bron: `Chat/*.swift`, `Views/ChatView.swift`, `Views/Composer.swift`, tests. **Er is géén** regenerate of het bewerken van berichten; niet toevoegen.

**Verzoek.** `POST {base}/v1/chat/completions`, geen `max_tokens`, velden `stream`, `stream_options.include_usage`, `temperature`
(standaard 1,0, schuif 0-1,5), `top_p:1`, `chat_template_kwargs{enable_thinking, reasoning_effort}`, tools indien aanwezig. Thinking uit:
`thinking_budget_tokens:0`. Aan met budget N: ook `reasoning_budget_message`. Keuzes 4K, 8K, 16K (standaard), 32K, onbeperkt. Time-out
3600 s. Reasoning gaat nooit terug naar het model. Afbeeldingen als JPEG-datacontent vóór de tekst.

**Doorwerken.** Maximaal 200 rondes met werkmap, 20 zonder. Bij >80% context eerst samenvatten. Na een beurt zonder tool-calls bepaalt
`looksUnfinished` of het model doorgaat. **Port deze functie regel voor regel, in deze volgorde**: afgekapt; geen tekst (alleen denken);
niet aan het werk → klaar; tool-call-resten in antwoord of de laatste 300 tekens van denken; eindigt op `?` → klaar; eindigt op `:` → niet
klaar; na stappen moet het eerste woord klaar/done/fertig/terminé zijn; zonder stappen de lijsten met aankondigingen uit de code. Maximaal 3
aansporingen op rij (reset bij een tool-call), getoond als één gecentreerd bijschrift. `finish_reason=length` verwerpt tool-calls.

**Context.** Meter bovenin (groen, amber >68%, rood >90%), popover met exacte aantallen en knop **Nu samenvatten**. Bij 80%, handmatig of bij een
400-contextfout: de snijregel, transcriptlimieten en het niet-streamende verzoek uit `ChatClient.swift` (temperatuur 0,3, `max_tokens` 2000). Berichten worden
nooit verwijderd: samenvatting + `summaryUpTo`, zichtbaar als amber lijn die uitklapt.

**Tools** (namen, schema's en foutteksten exact overnemen): `web_search`, `fetch_url`, `list_dir`, `read_file`, `write_file`, `edit_file`,
`run_command`. Limieten: `read_file` 2 MB zonder offset/tail, 40 000 tekens uitvoer, binair herkennen in eerste 8000 bytes + 64 bytes hex;
`edit_file` eist precies één match; `list_dir` 500; toolresultaat max. `max(8000, ctx×¾)` (¾ begin, ¼ eind).

**Rechtenmatrix.** Lezen/Schrijven/Uitvoeren, elk Toestaan, Eerst vragen, Weigeren. Standaard lezen toestaan, de rest vragen. Knop met drie vette letters
(nl L S U, en R W X, de L S A, fr L É X; groen/oranje/rood). Vraagkaart met Weigeren (Esc), Altijd in dit gesprek, Toestaan (Enter). "Altijd" geldt voor deze
chat en deze appsessie; weigeren gaat altijd voor. Stop weigert een open vraag.

**Sandbox.** Bestandstools alleen binnen de werkmap. **Port en breid uit voor Windows, met tests:** hoofdletterongevoelig, `..`, absolute paden, andere stations, UNC en
`\\?\`, symlinks en junctions (resolven met `GetFinalPathNameByHandle`), 8.3-namen, alternate data streams (`bestand.txt:stroom`), gereserveerde namen (`CON`,
`NUL`), `/` naast `\`, en `~`. `run_command` is niet gesandboxed, net als op de Mac.

**`run_command`.** Tijdslimiet 120 s, 1-1800. Max. 30 000 tekens (12 000 begin + eind). Elk commando in een Job Object, zodat de hele procesboom stopt.

**Systeemprompt.** Volgorde exact uit `ChatClient.swift`, inclusief de regels over KLAAR. **Herschrijf de Mac-delen voor Windows**, in alle 4 talen:
"Mac" → "pc"; `open` → `Start-Process`; `mdfind`, `ls`, `cat` → `Get-ChildItem -Recurse`, `Get-Content`, `where.exe`; `/Applications` → Program Files en Startmenu; `sudo`,
`dtruss` → geen UAC-prompts; `osascript` weg. Programma's met venster start het model losgekoppeld met `Start-Process`. Ontbreken Python of git, dan zegt de prompt dat (zoals `hasDeveloperTools` op de Mac).

**Notities en leren.** `WINAIMAGIC.md` in de werkmap (lees ook `MACAIMAGIC.md` als die er alleen is), algemene notities in de appdata-map. Elk verzoek opnieuw lezen, max. 8000 tekens. Na een
taak van minstens 3 werkmap-tools: het leerverzoek en de sectie *Geleerd* met marker `<!-- winaimagic:lessons -->` (accepteer ook `macaimagic:lessons`). Maximaal 5 per taak, 60 in totaal.
Overslaan na web, stop, fout of schrijven-geweigerd. Prompt blijft bevroren tot het bestand wijzigt, voor de prompt-cache.

**Web.** Brave als er een sleutel is, anders DuckDuckGo met Bing als terugval (blokkade-herkenning, 2 s tussenruimte, 600 s alleen Bing na blokkade). Parsers exact, `kl=nl-nl`, 8 resultaten. `fetch_url` 20 s, 3 MiB, 12 000 tekens. Weigertekst
exact. User-Agent: huidige Edge in plaats van Safari.

**UI.** Bovenbalk (modelchip, mapchip, contextmeter, sparkline, tok/s), engine-banners, lege chat met 4 suggesties, gesprekkenlijst, assistentkaart in de volgorde uit de code, stappen inklappen boven 3 ("37 stappen", laatste 2 zichtbaar), bronchips (max. 10), eigen eenvoudige
markdown-parser (geen tabellen; precies die uit `ChatView.swift`), kopieerknoppen, composer met Denken, Web, Werkmap, L S U, afbeeldingen via knop, slepen en plakken (Ctrl+V: afbeeldingsbestand eerst, bitmap alleen als het klembord geen tekst heeft), geanimeerde rand tijdens werk.

**Opslag.** `conversations.json` met dezelfde velden. Geen migratie van Mac-data nodig (dus ook geen MacDeepSeek-overnamecode). Datums in ISO 8601.

## 9. Functionele specificatie: documenten

Bron: `Documents/*.swift`, `Views/DocumentsView.swift`, tests. Dit is het grootste onderdeel. **Port de Word-engine eerst als losse bibliotheek met de tests en
`Fixtures/sjabloon.docx`**, voordat er UI is.

**Projecten.** Map met `project.json` (`{"fields":[{id,key,value}]}`), `huisstijl\<eigen naam>.docx|.dotx`, `bijlagen\*`, concepten `<titel>.md`, uitvoer `<titel>.docx` ernaast. Zelfde indeling als op de Mac, zodat projecten uitwisselbaar zijn. Nooit overschrijven (`Rapport 2.docx`). De naam van het huisstijlbestand blijft behouden.

**Pijplijn.** Bijlagen lezen → plan met zwaar model (bijlagen eerst, dan huisstijl, dan gebruikelijke opbouw; max. 5 zoekvragen, geen klantgegevens) → web → concept aanmaken → secties in volgorde met samenvatting als laatste → voorblad vullen → citaten en `# Bronnen` → pas na **Goedkeuren** de `.docx`. Stop bewaart wat er staat. Eén stille sectie stopt niets. **Requests bevatten geen `tools`.** Tokens, temperaturen en thinking per stap staan in de code.

**Privacy.** Uit zoekvragen verdwijnen e-mail, IBAN, bedragen, telefoon, BSN, lange getallen en de waarden van projectvelden. Jaartallen en artikelnummers blijven. Iedere pagina wordt beoordeeld tegen de vraag die haar vond; max. 8 pagina's, elke vraag krijgt zijn beurt.

**Word-engine.** Geen OpenXML SDK-herschrijving die onderdelen aanraakt: **onaangeroerde delen blijven byte-identiek** en ook geparste XML behoudt prefixen, attributen en commentaar (`XNode.swift` is bewust prefix-letterlijk). Port `Zip` (vaste datum 1980-01-01, reproduceerbaar), `DocxPackage`, `XNode`, `DocxModel`, `DocxBuilder`, `DocxFields`, `DocxCompose` één op één:

- Huisstijl leren uit voorbeelden (rollen, dominante `rPr` ≥60%, instructietekst uitsluiten), kopnamen in 10 talen.
- Alles vóór de eerste kop blijft: voorblad, inhoudsopgave, sectie-einde. Daarna nieuwe blokken vóór de laatste `sectPr`.
- Velden: custom properties, content controls, `{{x}} <<x>> «x»`, kop-/voetteksten, `[x]` en labelregels alleen vóór de body.
- Tabellen naar het voorbeeld, lijsten met eigen `w:num`, afbeeldingen alleen binnen het project, bijschrift met `SEQ`.
- Inhoudsopgave met echte regels, `PAGEREF` en `w:updateFields`.
- De validator rapporteert, en het bestand wordt toch geschreven.

Open de uitvoer ook echt in **Microsoft Word op Windows** (automatiseer via COM als Word aanwezig is): geen reparatiemelding, inhoudsopgave klopt, huisstijl klopt. Dat kon op de Mac niet zo direct.

**Editor.** Markdown-dialect uit `MarkdownBlocks.swift` (ook `<!-- pagebreak -->`, bijschriften). Scope: selectie, anders sectie, anders alles. Snelle acties Verbeter, Korter, Spelling met het snelle model; eigen instructie met het zware. Autosave na 600 ms, één niveau ongedaan maken, alleen-lezen tijdens werk. Monospace 13, geen slimme aanhalingstekens. Live meescrollen. De statusregels scrollen in een eigen ruimte van maximaal 92 + 110 hoog, zodat knoppen altijd bereikbaar blijven (test: 25 lange notities, 4000 regels).

## 10. Functionele specificatie: overige schermen

Bron: `AppState`, `Engine/*`, `Support/*`, `Views/*`.

**Shell.** Eén venster, min. 1000×680, altijd donker, eigen titelbalk. Zijbalk: logo, naam, gekozen model; Live, Modellen, Documenten, Vergelijken, Cluster, Systeem; gesprekken; mini-dashboard. Instellingen met tabbladen Chat en Engine (560×520). Startvolgorde uit de code; systeemcontrole opent bij een fout of de eerste keer.

**Modellen.** Raster van 2 kolommen, kaart met voortgangsring, badges, Download/Pauzeer, Kies, planbalk, plandetails, engine-, API- en mapkaart. Kies werkt zodra de headers binnen zijn: schrijf downloads dus **sequentieel**, zonder vooraf te reserveren.

**Downloads.** Eigen `HttpClient`-downloader in plaats van het shellscript, zelfde gedrag: per bestand parallel, 10 pogingen, hervatten met Range, exacte grootte, sha256, `stamp` patchen (hash vóór het stempelen), `.ok`-sentinel, 2 s status, EMA-snelheid. Sluit de gebruiker de app tijdens een download, dan hervat die bij de volgende start. Met de hand neergezette bestanden van de juiste grootte tellen mee, zodat modellen van een Mac niet opnieuw gedownload hoeven te worden. Daarna het pack bouwen. Op NTFS: paden boven 260 tekens, en kies bij een kleine `C:` actief een andere schijf.

**Vergelijken.** Procedure, lange prompt (36 notities, tijdstempel) en bewaarde velden uit `BenchmarkRunner.swift`. Toevoegen: VRAM en RAM. Grafieken en tabel als op de Mac.

**Systeem.** Bij elke start. Rijen: app, hardware (GPU en driver, VRAM, RAM, Windows-versie, gekozen backend met reden, compute capability of `gfx`-architectuur), engine-binaries (`--version`, Mark of the Web herstellen), download, model en schijf, **wisselbestand**, **NVMe of niet**, **Defender**, ontwikkeltools (optioneel, via `winget`). Statussen ok, fixed, todo, optional, failed; knop per actie.

**API.** `http://127.0.0.1:8080`: OpenAI `/v1`, Anthropic `/v1/messages`, Responses `/v1/responses`. Drie kopieerbare snippets, in PowerShell-vorm (`$env:ANTHROPIC_BASE_URL="http://127.0.0.1:8080"; claude`). `0.0.0.0` alleen met sleutel, en de eigen client gebruikt dan `127.0.0.1`. Snelle engine op poort + 1, altijd lokaal. Test met Claude Code op beide engines.

**Talen.** `L()`-systeem met Nederlandse sleutels, nl/en/de/fr, eerste start volgens Windows. Hergebruik `tools/i18n/*.json` en `tools/i18n.py`, pas alle Mac-teksten aan: van de 510 sleutels noemen er 41 "Mac", 7 Thunderbolt, 4 de GPU-limiet en 2 de Finder. Test: elke taal heeft elke sleutel. Getallen en datums volgen de taal van de app.

**Thema.** Kleuren: accent `#5C7AFF`, violet `#9E5CFF`, cyan `#33DBF2`, roze `#FF5CAD`, mint `#4DF29E`, amber `#FFB840`, nacht `#090A17`. Kaarten radius 18, padding 18, pagina 28, secties 18. Aurora-achtergrond (formule in `Theme.swift`), standaard stil. Orb draait alleen bij tokens. Pictogram: `scripts/make_icon.swift` nabouwen zonder Apple-symbool als dat niet mag.

**Instellingen.** Alle sleutels en standaarden uit `Settings.swift`, plus VRAM-reserve en wisselbestand-advies.

## 11. Gaten in de Mac-versie die je niet overneemt

Deze gevallen zijn gevonden bij het lezen van de code. Herstel ze op Windows en zet ze in `docs/PARITY.md`, zodat de opdrachtgever ze ook op de Mac kan oplossen:

| Mac-gedrag | WinAIMagic |
|---|---|
| Een `&`-proces houdt de pipe open en blokkeert `run_command` buiten de time-out om | Time-out werkt altijd; losgekoppelde programma's blijven lopen |
| Stop laat een lopend commando doorgaan | Stop stopt de procesboom |
| Na Stop blijft een `tool_call` zonder resultaat in de geschiedenis staan en draait de spinner door | Vul een resultaat "gestopt door de gebruiker" in |
| Pauzemelding "N stappen gezet" komt op een onzichtbaar bericht | Zichtbaar in de chat |
| Downloadfouten worden nergens getoond | Op de modelkaart, met wat er misging |
| `engine.log` bevat ongefilterde metadata met de verboden naam | Filter ook het logbestand |
| De helper blijft na afsluiten draaien | Stopt mee (Job Object) |
| Onleesbaar `conversations.json` wordt bij de volgende opslag overschreven | Bewaar als `.bad` en meld het |
| Brave-fout geeft "geen resultaten" | Melding, terugval op DuckDuckGo |
| API-sleutel staat zichtbaar in de procesargumenten | In de config |
| Zoekregio altijd Nederlands | Volgt de taal van de app |

**Eén toevoeging aan de functies:** een **Verwijder model**-knop met bevestiging. Bij modellen tot 460 GB is dat op Windows hard nodig. Verder geen nieuwe functies zonder toestemming. De extra's van upstream (Turbo, Saved, Ollama-routes in de UI, webdashboard) zijn buiten scope.

## 12. Plan van aanpak

Werk in deze volgorde. **Elke fase eindigt met een demo-rapport met metingen.** Na fase 0 stop je tot de opdrachtgever akkoord geeft.

**Fase 0: bewijs op de echte pc (go/no-go).** Nog geen UI.
1. Bouw de fork en LlamaEngine op Windows voor CUDA én HIP (Vulkan voor LlamaEngine), met de thinking-patch. Het bouwen moet voor beide lukken, ook al zit er maar één merk in de pc.
2. Stel het wisselbestand in, draai GLM Compact 24, DeepSeek Compact en MiniMax Compact vanaf een pack.
3. Probeer de vier Mac-bestanden uit §5 (DeepSeek Q4, GLM Compact, GLM Q4, Qwen3.8), plus Qwen3.6 via LlamaEngine.
4. Meet per model: schrijven en prompt lezen (zelfde methode als `BenchmarkRunner`), laadtijd, VRAM, RAM, SSD, koud en warm, voor en na kalibratie, plus één lange agentsessie op 64K.
5. Meet het tweede model naast het zware (§7).
6. Lever: een eerste `gpu-profiles.json` voor de aanwezige kaart, `docs/RESEARCH.md` voor Windows, definitieve catalogusvolgorde, aanbeveling, lijst met wat niet werkt, EULA-conclusie, en een voorstel als iets uit deze opdracht niet haalbaar blijkt.

**Fase 1: kern zonder UI.** `IGpuBackend` met `FakeGpuBackend`, `GGUFIndex`, `ModelCatalog`, `MemoryPlanner`, `ModelStore`, `IEngine` met beide implementaties, contracttests.

**Fase 2: shell.** Modellen, downloads, Live, Systeem, instellingen, thema, talen.

**Fase 3: chat.** Client, doorwerken, samenvatten, tools, rechten, sandbox, web, notities.

**Fase 4: documenten.** Word-engine (tests eerst), pijplijn, UI, Word-controle.

**Fase 5: rest.** Vergelijken, cluster, API-kaart.

**Fase 6: uitlevering.** Inno Setup-installer plus zip, sha256, licentiebestanden, schone-pc-test in een Windows-VM met GPU-passthrough of op een tweede schijf. Eigen repo `WinAIMagic` (bestaat nog niet; maak hem alleen met toestemming). Workflow als bij MacAIMagic: elke versie naar `main`, `RELEASE/` met nieuwe zip en `.sha256`, GitHub Release met tag `vX.Y.Z` en een vaste naam `WinAIMagic-Windows-x64.zip`. Modellen nooit in de repo.

**Ondertekenen:** de Apple-certificaten gelden hier niet. Zonder Authenticode-certificaat waarschuwt SmartScreen. Stel een keuze voor (Azure Trusted Signing of een OV-certificaat), met kosten. Dat is een besluit van de opdrachtgever. Sla nooit certificaten of sleutels op in de repo.

## 13. Licenties

- **MacAIMagic:** MIT; WinAIMagic ook. `LICENSE`, `THIRD_PARTY_NOTICES.md`.
- **Hoofdengine, ggml, exllamav3, beellama.cpp:** MIT. De copyrightvermelding moet **woordelijk** mee, ook "Project Maya contributors". Dat kan niet weg. Zet hem alleen in `THIRD_PARTY_NOTICES.md` in de installatiemap, zonder link of vermelding in de UI.
- **Modellen:** DeepSeek en GLM MIT.
- **MiniMax M3.1:** gratis voor niet-commercieel gebruik. Commercieel: zichtbaar "Built with MiniMax M3.1-Flash" en een eenmalige melding aan MiniMax, boven $20M jaaromzet schriftelijke toestemming, plus een lijst verboden gebruik. Toon de licentie voor de download.
- **Qwen:** eigen community-licentie. Toon vóór het downloaden. Bij een commerciële uitgave is juridisch advies nodig, omdat de licentie "AI Work Assistant"-diensten noemt.
- **Weglaten:** DFlash2 (CC BY-NC-ND), en het webdashboard van upstream met zijn eigen licenties.
- **NVIDIA en AMD:** alleen wat hun voorwaarden toestaan voor CUDA-, HIP- en ROCm-DLL's.

## 14. Klaar betekent

- [ ] Elke test uit `App/Tests` heeft een geslaagde tegenhanger. Afwijkingen staan in `docs/PARITY.md`.
- [ ] De `.docx`-uitvoer uit `sjabloon.docx` geeft nul validatorproblemen en opent in Word zonder reparatiemelding.
- [ ] Contracttests slagen op beide engines, inclusief thinking-budget per verzoek.
- [ ] Hetzelfde pakket herkent NVIDIA en AMD en kiest zelf de backend. `FakeGpuBackend`-tests slagen voor elk profiel, en geen merkcode buiten de GPU-laag.
- [ ] Op de referentie-pc: elk model uit de catalogus start. Echte metingen staan in de README en op de modelkaarten.
- [ ] Een C64-test of vergelijkbare agenttaak uit `docs/RESEARCH.md` §6 loopt van begin tot eind in een werkmap.
- [ ] Claude Code werkt via `ANTHROPIC_BASE_URL`.
- [ ] Schone Windows 11: installeren, model downloaden en chatten zonder iets anders te installeren dan de GPU-driver.
- [ ] Een geforceerd afsluiten laat geen engine, helper of Python-proces achter.
- [ ] `git grep -i maya` en een scan van het pakket vinden de naam alleen op de toegestane plekken (§2).
- [ ] Inactief CPU-gebruik onder 1%, en de UI blijft reageren tijdens elke lange taak.

## 15. Begin zo

1. Lees beide repo's.
2. Vul de hardware in §3 aan met een meting op de pc.
3. Geef in maximaal één pagina: wat er in deze opdracht niet klopt met de code die je las, welke risico's je ziet in fase 0, en je vragen.
4. Begin daarna aan fase 0.
