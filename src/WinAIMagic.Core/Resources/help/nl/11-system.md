---
title: Systeem
icon: Info
keywords: systeemcheck controle videokaart engine wisselbestand Defender schijfruimte NVMe aanbeveling
---
**Systeem** controleert bij elke start wat WinAIMagic nodig heeft:
- de versie van de app, de videokaart, het RAM en welke engine-backend past (CUDA, Vulkan of de processor);
- of de engines starten, en of het NVIDIA-pakket er is in de draagbare versie;
- het **wisselbestand**: de engine heeft er een grote nodig om het model vast te zetten;
- de schijf van de modelmap, en of het een snelle NVMe-schijf is;
- of Windows Defender de engine tegenhoudt.

Wat de app zelf kan oplossen, lost hij op. Voor de rest staat er een knop bij, zoals **Stel in** voor het wisselbestand (dat vraagt toestemming van Windows en telt na een herstart). **Opnieuw controleren** voert de controle nog een keer uit. Je ziet ook welk model het best bij deze pc past.
