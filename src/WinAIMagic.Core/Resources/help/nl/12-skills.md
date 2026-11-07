---
title: Skills
icon: Lightbulb
keywords: skills instructies SKILL.md eigen onderwerp
---
Een **skill** is een korte handleiding voor het model over één onderwerp, met commando's die op Windows 11 getest zijn. Het model ziet alleen de namen en laadt een skill pas als het hem nodig heeft. Zo blijft de prompt kort en het model snel.

## Aan of uit
Skills staan standaard aan. Uitzetten kan onder Instellingen → **Systeembeheer** → **Skills**.

## Ingebouwde skills
| Skill | Over |
|---|---|
| uiterlijk | donkere modus, accentkleur, taakbalk, Verkenner, bestandsextensies |
| energie | slaapstand, scherm uit, energiebeheerschema, batterijrapport |
| netwerk | adapters, DNS, wifi, verbinding testen, DNS-cache legen |
| software | programma's zoeken, installeren, bijwerken en verwijderen met winget |
| opstartitems | wat met Windows meestart, geplande taken, diensten |
| opslag | wat ruimte inneemt, veilig opruimen via de Prullenbak |
| privacy-beveiliging | firewall, Defender, BitLocker, Gebruikersaccountbeheer |
| diagnose | traag, processor en geheugen, fouten in het logboek, schijven |

## Een eigen skill maken
Het makkelijkst: vraag het in de chat, bijvoorbeeld "maak een skill voor programmeren in C64-assembler, met de bestanden in deze map". Het model schrijft de skill, je ziet hem op een kaart, en na **Toestaan** staat hij in de lijst. Zo pas je een eigen skill ook later aan.

Zelf maken kan ook:
1. Instellingen → Systeembeheer → **Open map**.
2. Maak een map, bijvoorbeeld `mijn-printer`, met daarin een bestand `SKILL.md`:
```
---
name: mijn-printer
description: De printer op kantoor installeren en herstellen
platform: windows
---
Schrijf hier de stappen en commando's.
```
3. Een eigen skill met dezelfde naam als een ingebouwde, vervangt die ingebouwde.
