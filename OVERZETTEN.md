# WinAIMagic overzetten naar een andere pc

Hoe je deze map naar een andere Windows-pc brengt en daar met Claude Code verder werkt, ook met de builds en de releases.

## Wat waar staat

| Wat | Waar (op deze pc) | In git? |
|---|---|---|
| De broncode, CLAUDE.md, docs, scripts, tests | `D:\04. AI\WinAIMagic` | ja: `github.com/mfidder2026/WinAIMagic`, branch `main` |
| Gebouwde engines (main, second, image, speech, vision) | `build\` | nee |
| Opgehaalde broncode van de engines | `engine\llama.cpp`, `engine\tiered\third_party\llama.cpp`, `engine\image-engine`, `engine\speech-engine` | nee (de fetch-scripts halen ze op één vaste commit) |
| De laatste uitgepakte app en de releasebestanden | `dist\` | nee |
| `bin\` en `obj\` van .NET | in elk project | nee, die maakt `dotnet build` opnieuw |
| Gesprekken met Claude Code over dit project | `C:\Users\<jij>\.claude\projects\D--04--AI-WinAIMagic\` (`*.jsonl`) | nee |
| Je eigen WinAIMagic: instellingen, gesprekken, skills (ook de C64-skills), modellen | `%LOCALAPPDATA%\WinAIMagic` | nee, en hoort er niet in |
| De Mac-versie, voor het overnemen van wijzigingen | `github.com/mfidder2026/MacAIMagic` | ja, apart |

## 1. Programma's op de nieuwe pc

Installeer, met dezelfde versies als hier waar dat kan:

- **Git** en **GitHub CLI** (`gh`, hier 2.103.3). Log daarna in met `gh auth login`.
- **.NET 10 SDK** (hier 10.0.404).
- **Python 3.13** op het PATH (hier 3.13.14). De tests starten de neppe servers van de Mac ermee.
- **Claude Code** (de desktop-app of de CLI), ingelogd met hetzelfde account.

Alleen nodig om een release te bouwen (`scripts/package.py`):

- **Inno Setup 6**, in `%LOCALAPPDATA%\Programs\Inno Setup 6` (daar zoekt `package.py` het).
- **CUDA Toolkit 13.4** (`package.py` kopieert er de cuBLAS-DLL's uit; andere plek: zet `CUDA_PATH`).

Alleen nodig om de engines opnieuw te bouwen (`scripts\build-*.bat`):

- **Visual Studio 2022 Build Tools** met C++ (de scripts roepen `vcvars64.bat` aan), **CMake** en **Ninja**.
- **Vulkan SDK**, voor de Vulkan-builds.

## 2. De map overzetten

Twee manieren, allebei goed.

**A. Alles kopiëren (sneller, de engines hoeven niet opnieuw gebouwd te worden)**

1. Kopieer de hele map `D:\04. AI\WinAIMagic` naar **hetzelfde pad** op de nieuwe pc, met de verborgen map `.git`.
   `bin\`, `obj\` en `dist\` (hier 47 GB, vooral oude releases) mag je weglaten: die komen terug bij het bouwen.
   Neem `build\` (12 GB) en `engine\` (1,1 GB) wel mee; anders moet je de engines opnieuw bouwen.
2. Controleer in die map:

```bash
git status
```

```bash
git pull
```

**B. Opnieuw ophalen van GitHub (schoner, maar de engines moet je opnieuw bouwen)**

```bash
git clone https://github.com/mfidder2026/WinAIMagic "D:\04. AI\WinAIMagic"
```

Daarna de engines: de fetch-scripts (`scripts/fetch-*.sh`, in Git Bash) en de build-scripts (`scripts\build-llama.bat cuda`, enzovoort; zie CLAUDE.md onder Image engine en Speech engine). Kopieer anders alleen `build\` en `engine\` van deze pc erbij.

Zet op de nieuwe pc dezelfde git-naam en hetzelfde e-mailadres als hier. Hier lees je ze af met `git config user.name` en `git config user.email`:

```bash
git config --global user.name "Fremen IT Workers"
```

```bash
git config --global user.email "<hetzelfde adres als hier>"
```

## 3. Claude Code verder laten gaan

- **CLAUDE.md** zit in git en gaat dus vanzelf mee: Claude leest het bij elke sessie. Daarin staan de regels (commits, de Mac-versie, tests, releases).
- **Wat gebeurd is**, staat in de commits en in `docs/PARITY.md`: de laatste overgenomen Mac-commit staat bovenaan. Een nieuwe sessie kan daarmee verder; zeg bijvoorbeeld "werk bij met de Mac-versie".
- **Eerdere gesprekken met Claude** (optioneel): kopieer `C:\Users\<jij>\.claude\projects\D--04--AI-WinAIMagic\` naar dezelfde plek op de nieuwe pc. Dat werkt alleen als de projectmap daar ook `D:\04. AI\WinAIMagic` is: Claude Code noemt de map naar dat pad. Dan zie je de oude sessies terug.
- Claude heeft voor dit project geen eigen geheugenbestanden (`memory\`): alles staat in CLAUDE.md en de repository.

## 4. Controleren dat het werkt

In de projectmap:

```bash
dotnet test tests/WinAIMagic.Engine.Tests
```

Daarna ook `WinAIMagic.Chat.Tests`, `WinAIMagic.Studio.Tests` en `WinAIMagic.Documents.Tests`. De Chat-tests hebben Python op het PATH nodig.

```bash
python scripts/i18n_check.py
```

Een release bouwen (alleen als het moet):

```bash
python scripts/package.py --portable
```

## 5. Je eigen WinAIMagic (los van de code)

Wil je ook je eigen gebruik van de app meenemen (instellingen, gesprekken, je skills zoals de C64-skills, en de modellen), kopieer dan `%LOCALAPPDATA%\WinAIMagic`.

- De modellen zijn groot (tientallen GB). Je kunt ze ook in de app opnieuw downloaden.
- API-sleutels in `settings.json` zijn versleuteld voor deze Windows-gebruiker op deze pc. Op de nieuwe pc moet je ze opnieuw invullen. Dat geldt ook voor de wachtwoorden van connectors: die staan in Windows' Referentiebeheer, niet in de map.
- De C64-tools van de skills (Java, VICE, Kick Assembler) staan in `OneDrive\dev\c64`. Met OneDrive komen ze vanzelf mee.

## Let op

- Op de nieuwe pc kan een andere videokaart zitten. De app rekent zijn geheugenplan zelf uit; de tests beschrijven elke pc en hebben geen kaart nodig.
- Een release (`gh release create`) maakt Claude alleen als je erom vraagt (CLAUDE.md).
