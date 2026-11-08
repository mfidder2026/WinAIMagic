# WinAIMagic

Draai enorme AI-modellen volledig op je eigen Windows 11-pc. Een model dat niet in je videokaart past, start gewoon en haalt de rest uit het RAM en van de SSD. Niets verlaat je computer, behalve zoekopdrachten als je web zoeken aanzet en de modeldownloads.

Dit is de Windows-versie van [MacAIMagic](https://github.com/mfidder2026/MacAIMagic). Eén setup: geen Python, Visual Studio of CUDA Toolkit, alleen de driver van je videokaart. Het draait de grote mixture-of-experts-modellen (GLM, DeepSeek, Qwen, MiniMax) en kleine modellen voor een laptop, zoals Ling-3.0-tiny, dat op vier processorthreads ongeveer 20 tok/s schrijft. De Engelse uitleg staat in de [README](../README.md).

## Installeren

1. Download `WinAIMagic-Setup-*.exe` bij [Releases](https://github.com/mfidder2026/WinAIMagic/releases/latest). Met de `.sha256` ernaast kun je het bestand controleren.
2. Start het bestand en kies een map voor de modellen, het liefst op een snelle NVMe-schijf. Beheerdersrechten zijn niet nodig. Een nieuwere setup installeert over een oudere versie heen en houdt je instellingen, gesprekken en modellen.
3. Windows SmartScreen kan waarschuwen, omdat de setup nog niet ondertekend is. Kies dan **Meer informatie** en **Toch uitvoeren**.
4. Op een pc met een NVIDIA-kaart biedt het scherm **Systeem** het NVIDIA-pakket aan (ongeveer 1,6 GB). De app downloadt het uit dezelfde release en controleert het; het werkt na een herstart. Een update met dezelfde NVIDIA-engines houdt ze. Zonder pakket rekent de app op de processor of via Vulkan, en draaien de grote modellen die de hoofdengine nodig hebben niet.
5. Open in de app **Modellen**, download een model en druk op **Start engine**.

Je hebt alleen de driver van je videokaart nodig. Python, Visual Studio en de CUDA Toolkit komen niet op je pc.

API-sleutels zijn versleuteld voor je Windows-account: op een andere pc vul je ze opnieuw in.

Het scherm **Systeem** laat zien wat je pc nog nodig heeft. Vaak is dat een groter wisselbestand: minstens je VRAM plus 9 GB. De knop daarvoor vraagt om toestemming en werkt na een herstart.

## Wat het kan

- **Chatten** met een limiet op het denken, afbeeldingen, web zoeken en een contextmeter.
- **Werken in een map:** het model leest en schrijft bestanden en voert commando's uit, met jouw toestemming per soort actie.
- **Documenten:** van notities en bijlagen naar een Word-bestand in je eigen huisstijl.
- **Beeld:** afbeeldingen maken van een beschrijving, op je eigen pc.
- **Vergelijken:** dezelfde meting op elk model, op je eigen pc.
- **Lokale API** op `http://127.0.0.1:8080`, voor OpenAI-, Anthropic- en Responses-clients.
- **Modelmap kiezen en wijzigen,** ook met modellen erin, zonder opnieuw te downloaden.
- **Een model verwijderen of bijwerken** op zijn kaart. Een gedownload model heeft **Controleer op update**: in een paar seconden, zonder de bestanden van het model te lezen, vergelijkt het ze met de catalogus van deze versie en vraagt het de server van de maker. Een update begint meteen; anders zegt de kaart dat er geen is.
- **Systeembeheer.** Het model kan deze pc bekijken (één PowerShell-commando dat alleen leest) en instellingen wijzigen met een plan dat jij goedkeurt, met per stap de huidige waarde op de kaart. Stappen met beheerdersrechten delen één vraag van Windows (UAC), kunnen eerst een herstelpunt maken, en het model maakt een wijziging ongedaan als je dat vraagt. Ingebouwde **skills** geven het geteste commando's per onderwerp (uiterlijk, energie, netwerk, software, opstartitems, opslag, beveiliging, diagnose); je kunt eigen skills toevoegen. Beide staan standaard aan onder Instellingen, Systeembeheer.
- **Hulp** in de app: een pagina Hulp, een ? bovenaan voor het scherm dat open is, en F1.
- **De app kent zichzelf.** Vraag in de chat wat je in WinAIMagic gedaan wilt hebben ("download het model dat bij deze pc past", "zet het denken uit", "maak een skill voor C64-assembler", "maak een afbeelding van ...") en het model doet het, na jouw goedkeuring op een kaart. Het leest daarvoor de eigen hulp en de stand van de app. Zijn eigen rechten, API-sleutels en het serveradres kan het niet wijzigen.
- **Connectors (MCP):** gereedschap van andere programma's en diensten, in het mcpServers-formaat van Claude Desktop: Bestanden, Web ophalen en GitHub als voorbeeld, je eigen, of overgenomen uit Claude Desktop en Claude Code. Het model laadt een connector pas als het hem nodig heeft; tools die alleen lezen mogen, de rest vraagt eerst, per connector. Diensten op internet loggen in met OAuth in je eigen browser, en tokens komen in Windows Referentiebeheer.
- **Taken en heartbeat:** het model voert op een vast moment een taak uit (eenmalig, dagelijks of op werkdagen, wekelijks, elke paar uur), gepland op de pagina Taken of door het in een gesprek te vragen, elke keer een eigen gesprek met een melding van Windows. Een heartbeat loopt af en toe een checklist na en meldt zich alleen als er iets is (standaard uit). Met taken of een heartbeat aan blijft de app na het sluiten in het systeemvak; hij kan opstarten met Windows.
- **Gesprekken tegelijk:** elk gesprek heeft een eigen run, goedkeuringskaart en stopknop; ze wisselen elkaar af voor het model. Bij groter werk staat er een stappenlijst boven het invoerveld.
- **Telegram:** praat via een eigen Telegram-bot met het model op je pc, ook als je weg bent: je koppelt je account met een code, keurt wijzigingen goed met knoppen en krijgt daar de uitkomst van taken. De token staat in Windows Referentiebeheer; het model blijft op de pc, de berichten gaan via Telegram.
- **Agent (standaard uit):** het model leest, zoekt en maakt concepten in klassiek Outlook (versturen vraagt altijd), surft in een eigen venster met eigen cookies (knoppen vragen eerst, kopen en versturen elke keer, nooit een wachtwoordveld), en bestuurt alleen de programma's die jij toestaat, via UI Automation. Antwoorden kunnen worden voorgelezen met de stemmen van Windows, en je kunt een vraag inspreken: het spraakmodel (Whisper, één keer gedownload) verstaat hem op deze pc, ook spraakberichten uit Telegram.
- **Cluster:** meerdere pc's samen, voor modellen op de tweede engine.

## Gemeten snelheid

Op één pc: RTX 5060 Ti 16 GB op PCIe 3.0 x8, Core i9-10900K, 128 GB DDR4-2400 in twee kanalen, NVMe.

| Model | Laden | Prompt lezen | Schrijven |
|---|---|---|---|
| Qwen3.6-35B-A3B Q4 | 15 s | 380 tok/s | **39,5 tok/s** |
| GLM-5.3-Flash Compact 24 | 98 s | 354 tok/s | **8,9 tok/s** |
| Ministral-3-8B Q4 | 4 s | 3.260 tok/s | **68,0 tok/s** |

| Beeldmodel | Eén afbeelding van 1024 x 1024, met laden | Beeldtokens |
|---|---|---|
| FLUX.2 Klein 4B | **9,1 s** | 5.366 per seconde |

De eigenaar mat alle vier opnieuw met 0.2.6 op 5 november ([ruwe gegevens](measurements/2026-11-05-app-0.2.6.json)). GLM schreef toen 8,5 tok/s. Qwen3.6 schreef 31,4 tok/s, met de experts van 28 lagen op de processor in plaats van 23; waarom het plan toen minder lagen op de kaart zette, is nog niet uitgezocht.

Met **Experts overslaan**, een keuze naast de startknop op het scherm Modellen, schrijft GLM op deze pc 11,3 (Licht) of 12,2 tok/s (Sterk), maar de antwoorden wijken dan iets af van het volledige model. Standaard staat dit uit, en Vergelijken toont elke keuze op een eigen regel.

Op jouw pc kan dat flink anders zijn. Meet het zelf onder **Vergelijken**. De overige modellen zijn hier nog niet gedraaid en AMD is nog niet gemeten.

### Een laptop met weinig geheugen

Ministral-3-8B Q4 (5 GB) is het model voor een pc met 8 of 16 GB RAM. Gemeten op dezelfde pc, met limieten die een laptop nabootsen, dus niet op een echte laptop. De snelheid op de processor van een echte laptop zal afwijken.

| Bootst na | Context | Schrijven |
|---|---|---|
| Een videokaart van 16 GB | 8K tot 32K | **72 tok/s** |
| Een videokaart van 8 GB: 28 van de 34 lagen op de kaart | 8K | **20 tok/s** |
| Geen videokaart, 16 GB RAM: 4 processorthreads | 16K in de app | **5 tok/s** |

Op 8 GB RAM zonder videokaart heeft het model ongeveer 5 GB vrij nodig. Met een limiet van 5 GB op wat het in het geheugen mag houden schrijft het nog 5 tok/s, met 4,5 GB zakt dat naar 0,7 tok/s, ook bij een kleinere context, en een echte SSD maakt dat nog erger. Sluit daar dus andere programma's.

**Ling-3.0-tiny Q4** (4,9 GB, MIT) is het model voor een pc met 6 tot 12 GB RAM, waar Ministral zou streamen. Het is een MoE van 8B waarvan per token 1B meedoet, en 18 van zijn 24 lagen hebben lineaire attention. Daardoor is zijn cache klein (9 KB per token) en streamen zijn experts goed. Gemeten op dezelfde pc op dezelfde manier, een prompt van 3.100 tokens en 300 tokens:

| Bootst na | Context | Prompt lezen | Schrijven |
|---|---|---|---|
| Een videokaart, alles op de kaart | 16K | 7.770 tok/s | **222 tok/s** (Ministral 65,2) |
| Geen videokaart: 4 processorthreads | 8K | 128 tok/s | **21,0 tok/s** (Ministral 3,3) |
| Geen videokaart, hooguit 4,5, 3,5 of 2,5 GB in het geheugen | 8K | 117 tot 126 tok/s | **20,5, 19,8 en 17,1 tok/s** |

Het bestand bleef op deze pc, die ruim RAM heeft, in de bestandscache van Windows. Een echte pc van 8 GB leest vaker van zijn SSD, en hoeveel trager dat is, is niet gemeten. In de app, op een nagebootste pc van 8 GB zonder videokaart, antwoordde het met 19,3 tok/s bij 16K context, waarvan de systeemprompt en de tools 5K nemen, schreef het een bestand en vroeg het toestemming voor het uitvoeren. Het is een klein model en maakt fouten: van vijf Nederlandse vragen had het de sommen en Parijs goed en noemde het de verkeerde schrijver van "De avonden". Het verving Nanbeige4.2-3B, dat op dezelfde vragen slechter Nederlands schreef, twee keer nadacht zonder te antwoorden en op de processor 2,8 tok/s schreef. De pc van 6 GB is alleen berekend.

**De 8-bit-cache.** Met een videokaart bewaart de tweede engine wat hij van het gesprek onthoudt (de KV-cache) in 8 bits in plaats van 16. Gemeten met Ministral op deze pc:

- Bij 32K context gebruikt hij 8,6 GB VRAM in plaats van 10,6; bij 8K 6,8 in plaats van 7,3.
- Op een videokaart van 8 GB passen daardoor 28 lagen in plaats van 24. Hij schrijft dan 20,0 in plaats van 14,0 tok/s en leest prompts met 1.663 in plaats van 1.296 tok/s.
- Met het hele model op de kaart kost het een beetje snelheid: 72,5 in plaats van 74,7 tok/s, en prompts 1-2% trager.
- De antwoorden blijven gelijk: op een tekst van 28.000 tokens koos het model op 98,8% van de plaatsen hetzelfde volgende woord.

Zonder videokaart blijft het 16 bits. Met 4 processorthreads las 8 bits een prompt na 4.000 tokens met 8 in plaats van 23 tok/s. Voor Qwen3.6 scheelt het maar 0,3 GB bij 32K, omdat dat model weinig cache heeft. De tabellen hierboven zijn gemeten met de 16-bit-cache.

**Stuur je meting.** Laptops kunnen we zelf niet meten. Na een meting onder **Vergelijken** opent **Deel op GitHub** [een formulier](https://github.com/mfidder2026/WinAIMagic/issues/new?template=meting.yml) waarin je het geëxporteerde bestand sleept. Er gaat niets weg tot je het daar zelf verstuurt.

### Afbeeldingen maken

FLUX.2 Klein 4B maakt afbeeldingen op het scherm **Beeld**. De download is 5,3 GB (het beeldmodel, een tekstmodel en een decoder). Gemeten op dezelfde pc met 4 stappen, met limieten die een kleinere pc nabootsen:

| Bootst na | Afbeelding | Tijd | Geheugen |
|---|---|---|---|
| Een videokaart van 16 GB | 1024 x 1024 | **8 s** | 8,1 GB op de kaart |
| Een videokaart van 8 GB | 1024 x 1024 | **9 s** | 3,9 GB op de kaart |
| Geen videokaart, 4 processorthreads | 512 x 512 | **2,5 min** | 2,2 GB eigen geheugen |
| Geen videokaart, 4 processorthreads | 768 x 768 | **5 min** | 4,4 GB eigen geheugen |
| Geen videokaart, 4 processorthreads | 1024 x 1024 | **9 min** | 7,5 GB eigen geheugen |
| Geen videokaart, 4 threads, laatste stap in delen | 1024 x 1024 | **12 min** | 1,4 GB eigen geheugen |

FLUX staat ook op het scherm Modellen (**Kies voor afbeeldingen**) en bij **Vergelijken**, dat op elke pc dezelfde afbeelding van 1024 x 1024 maakt: 10 s met laden op de test-pc, ongeveer 5.000 beeldtokens per seconde. Terwijl het tekent, telt de snelheidsmeter die beeldtokens. Een Engelse beschrijving werkt het best. Met **Verbeteren met web** schrijft het snelle model (Snel) die Engelse beschrijving eerst, en zoekt het zo nodig op het web hoe iets eruitziet; alleen de zoekvraag en de pagina's die het leest gaan naar buiten. Het scherm toont elke stap met het model dat hem doet, en de balk bovenaan noemt elk model dat in het geheugen staat.

Elke modelkaart heeft dezelfde rolknoppen: **Zwaar** (het chatmodel), **Snel** (het snelle model van Documenten) en **Afbeeldingen**. Documenten toont alle drie, met een groene stip zolang ze draaien.

Naast het eigen geheugen leest de engine de modelbestanden van schijf; 512 x 512 duurde even lang toen hij maar 2,5 GB in het geheugen mocht houden. De processor en SSD van een echte laptop wijken af. De app kiest een grootte en werkwijze die bij de pc passen, en de chatengine stopt zolang er een afbeelding wordt gemaakt: voor allebei is meestal geen ruimte.

## Wat nog niet werkt

Zie [PARITY.md](PARITY.md) voor de hele lijst. De belangrijkste punten:

- AMD-kaarten gebruiken alleen de tweede engine via Vulkan.
- Afbeeldingen zijn getest op GLM (hoofdengine) en Qwen3.6 (tweede engine). Op DeepSeek en MiniMax zijn ze aangesloten, maar nog niet met een echte afbeelding gedraaid. Elk model haalt zijn afbeeldingsbestanden apart op (ongeveer 1 tot 1,7 GB).
- De setup is nog niet ondertekend en nog niet getest op een schone pc.
- De cluster is alleen getest met een helper op dezelfde pc, nog niet met twee aparte pc's.
