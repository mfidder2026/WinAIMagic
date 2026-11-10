---
title: Instellingen
icon: Settings
keywords: instellingen geheugen gesprek bewaren taal voorkeuren context vision MTP poort adres VRAM-reserve wisselbestand backend systeembeheer skills connectors taken heartbeat systeemvak opstarten
---
Open de instellingen met het tandwiel rechtsboven.

## Chat
- **Taal**: Nederlands, Engels, Duits of Frans. Het model krijgt zijn instructies in dezelfde taal. De hulp is er in het Nederlands en het Engels.
- **Antwoorden**: denkniveau, maximaal nadenken, temperatuur, systeemprompt, en algemene notities (WINAIMAGIC.md).
- **Werkmap**: werken in een map aan/uit, onthouden wat het model leert, en de rechten L S U.
- **Web zoeken**: een eigen Brave Search-sleutel.

## Engine
- **Context**: van 32K tot 1M tokens. Meer context kost meer geheugen.
- **Afbeeldingen (vision)**, **MTP**, **Experts overslaan** (GLM), **Engine starten bij openen app**, **Bewegende achtergrond**.
- **Gesprekken in de engine bewaren** (standaard aan): wat de engine van een gesprek gelezen heeft, gaat naar de SSD zodra een ander gesprek, een taak of Documenten het model krijgt, na elk antwoord, en als de engine stopt. Ga je later verder, ook na een herstart, dan leest het model dat gesprek niet opnieuw. Bij een groot model scheelt dat minuten. Een lang gesprek neemt een paar honderd MB tot ruim een GB. Het neemt hooguit 40 GB (en nooit meer dan een tiende van de vrije schijf); de oudste gaan eerst, en een verwijderd gesprek neemt het zijne mee. **Wis** maakt de ruimte meteen vrij. Niet bij een cluster.
- **Videokaart**: de VRAM-reserve, het wisselbestand en de engine-backend.
- **Server**: adres, poort en API-sleutel (zie *Lokale API*).

## Systeembeheer
**Skills** en **Deze pc**: of het model deze pc mag bekijken en instellen, en de rechten om het systeem te lezen en te wijzigen. Ze staan standaard aan. De ? in dat tabblad opent *Systeembeheer*.

## Connectors
Gereedschap van andere programma's en diensten, met rechten per connector (zie *Connectors*).

## Taken
De **heartbeat** (aan of uit, hoe vaak, tussen welke uren, en of het model ervoor mag laden), **Op de achtergrond** (in het systeemvak blijven, het model stoppen als het venster dichtgaat, opstarten met Windows) en of het model na een taak weer stopt (zie *Taken en heartbeat*).
