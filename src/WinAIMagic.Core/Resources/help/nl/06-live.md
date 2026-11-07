---
title: Live dashboard
icon: Speed
keywords: snelheid tokens per seconde GPU CPU VRAM RAM geheugen SSD grafiek meter
---
**Live** laat zien wat de pc doet terwijl het model werkt:
- de **snelheidsmeter**: tokens per seconde tijdens het schrijven;
- grafieken van de **videokaart** en de **processor**, het **VRAM**, het **RAM** en wat er van de **SSD** wordt gelezen;
- onderin de zijbalk een mini-versie; klik erop om Live te openen.

## Twee snelheden
- **Schrijven** (decode): hoe snel het antwoord verschijnt.
- **Prompt lezen** (prefill): hoe snel het model je vraag, de bijlagen en de webpagina's leest. Bij lange taken is dit vaak de bottleneck.

> Leest het model veel van de SSD, dan past het niet helemaal in VRAM + RAM. Kies dan een compact model, sluit andere programma's, of zet Experts overslaan aan voor GLM (Instellingen → Engine).

Een volle videokaart geeft op Windows geen foutmelding, maar de snelheid zakt dan sterk. Daarom houdt de app een VRAM-reserve vrij; die stel je in onder Instellingen → Engine.
