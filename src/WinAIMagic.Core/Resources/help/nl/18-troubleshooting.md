---
title: Problemen oplossen
icon: Repair
keywords: probleem fout werkt niet traag start niet vastgelopen geheugen VRAM download mislukt Defender UAC log
---
## De engine start niet
- Open **Systeem** en kijk wat er oranje staat; meestal staat er een knop bij.
- Houdt Windows Defender de engine tegen, dan opent **Open Windows-beveiliging** het juiste scherm.
- Sluit grote programma's en games, en kies zo nodig een kleiner model.

## Schrijven is traag
- Kijk onder **Live** of er veel van de SSD wordt gelezen, en of het VRAM vol is. Dan past het model niet helemaal: kies een compact model of sluit andere programma's.
- Een te klein wisselbestand maakt de engine veel trager: **Systeem** zegt het en biedt **Stel in**.
- Zet **Denken** uit voor eenvoudige vragen.
- Een lange context maakt het prompt lezen trager. Begin een nieuw gesprek, of laat het gesprek samenvatten.

## Het model stopt halverwege of herhaalt zich
Zeg "ga door", of geef een aanwijzing. Bij werk in een map helpt een korte notitie in WINAIMAGIC.md.

## Een download mislukt
Druk nog eens op **Download**: de download gaat verder waar hij was. Zegt de kaart dat er een nieuwere versie is, gebruik dan **Update**.

## Een systeemwijziging is niet gelukt
- Het model zegt na afloop welke stap mislukte; dat staat ook bij de stappen in het gesprek.
- Vraag het model om de wijziging ongedaan te maken: dan gaan de stappen terug die wel zijn uitgevoerd.
- Heb je de vraag van Windows (UAC) met Nee beantwoord, dan is er niets gewijzigd.

## De vraag van Windows (UAC) verschijnt niet
Hij kan achter het venster van WinAIMagic staan. Kijk in de taakbalk, of druk op Alt+Tab.

## Nog steeds vast?
Open **Vergelijken → Exporteer meting**, of kijk in `%LOCALAPPDATA%\WinAIMagic\engine.log`.
