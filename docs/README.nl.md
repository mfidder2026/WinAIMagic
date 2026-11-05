# WinAIMagic

Draai enorme AI-modellen volledig op je eigen Windows 11-pc. Een model dat niet in je videokaart past, start gewoon en haalt de rest uit het RAM en van de SSD. Niets verlaat je computer, behalve zoekopdrachten als je web zoeken aanzet en de modeldownloads.

Dit is de Windows-versie van [MacAIMagic](https://github.com/mfidder2026/MacAIMagic). De Engelse uitleg staat in de [README](../README.md).

## Installeren

1. Download `WinAIMagic-Setup-*.exe` bij [Releases](https://github.com/mfidder2026/WinAIMagic/releases/latest) (ongeveer 1,8 GB).
2. Start het bestand en kies een map voor de modellen, het liefst op een snelle NVMe-schijf. Beheerdersrechten zijn niet nodig.
3. Windows SmartScreen kan waarschuwen, omdat de setup nog niet ondertekend is. Kies dan **Meer informatie** en **Toch uitvoeren**.
4. Open in de app **Modellen**, download een model en druk op **Start engine**.

Je hebt alleen de driver van je videokaart nodig. Python, Visual Studio en de CUDA Toolkit komen niet op je pc.

Het scherm **Systeem** laat zien wat je pc nog nodig heeft. Vaak is dat een groter wisselbestand: minstens je VRAM plus 9 GB. De knop daarvoor vraagt om toestemming en werkt na een herstart.

## Wat het kan

- **Chatten** met een limiet op het denken, afbeeldingen, web zoeken en een contextmeter.
- **Werken in een map:** het model leest en schrijft bestanden en voert commando's uit, met jouw toestemming per soort actie.
- **Documenten:** van notities en bijlagen naar een Word-bestand in je eigen huisstijl.
- **Vergelijken:** dezelfde meting op elk model, op je eigen pc.
- **Lokale API** op `http://127.0.0.1:8080`, voor OpenAI-, Anthropic- en Responses-clients.
- **Modelmap kiezen en wijzigen,** ook met modellen erin, zonder opnieuw te downloaden.
- **Cluster:** meerdere pc's samen, voor modellen op de tweede engine.

## Gemeten snelheid

Op één pc: RTX 5060 Ti 16 GB op PCIe 3.0 x8, Core i9-10900K, 128 GB DDR4-2400 in twee kanalen, NVMe.

| Model | Laden | Prompt lezen | Schrijven |
|---|---|---|---|
| Qwen3.6-35B-A3B Q4 | 15 s | 380 tok/s | **39,5 tok/s** |
| GLM-5.3-Flash Compact 24 | 98 s | 354 tok/s | **8,9 tok/s** |

Met **Experts overslaan**, een keuze naast de startknop op het scherm Modellen, schrijft GLM op deze pc 11,3 (Licht) of 12,2 tok/s (Sterk), maar de antwoorden wijken dan iets af van het volledige model. Standaard staat dit uit, en Vergelijken toont elke keuze op een eigen regel.

Op jouw pc kan dat flink anders zijn. Meet het zelf onder **Vergelijken**. De overige modellen zijn hier nog niet gedraaid en AMD is nog niet gemeten.

### Een laptop met weinig geheugen

Ministral-3-8B Q4 (5 GB) is het model voor een pc met 8 of 16 GB RAM. Gemeten op dezelfde pc, met limieten die een laptop nabootsen, dus niet op een echte laptop. De snelheid op de processor van een echte laptop zal afwijken.

| Bootst na | Context | Schrijven |
|---|---|---|
| Een videokaart van 16 GB | 8K tot 32K | **72 tok/s** |
| Een videokaart van 8 GB: 24 van de 34 lagen op de kaart | 8K | **14 tok/s** |
| Geen videokaart, 16 GB RAM: 4 processorthreads | 16K in de app | **5 tok/s** |

Op 8 GB RAM zonder videokaart heeft het model ongeveer 5 GB vrij nodig. Met een limiet van 5 GB op wat het in het geheugen mag houden schrijft het nog 5 tok/s, met 4,5 GB zakt dat naar 0,7 tok/s, ook bij een kleinere context, en een echte SSD maakt dat nog erger. Sluit daar dus andere programma's.

## Wat nog niet werkt

Zie [PARITY.md](PARITY.md) voor de hele lijst. De belangrijkste punten:

- AMD-kaarten gebruiken alleen de tweede engine via Vulkan.
- Afbeeldingen zijn getest op GLM (hoofdengine) en Qwen3.6 (tweede engine). Op DeepSeek en MiniMax zijn ze aangesloten, maar nog niet met een echte afbeelding gedraaid. Elk model haalt zijn afbeeldingsbestanden apart op (ongeveer 1 tot 1,7 GB).
- De setup is nog niet ondertekend en nog niet getest op een schone pc.
- De cluster is alleen getest met een helper op dezelfde pc, nog niet met twee aparte pc's.
