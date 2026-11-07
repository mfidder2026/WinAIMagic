---
title: Connectors
icon: Connect
keywords: connectors MCP gereedschap GitHub bestanden web ophalen importeren Claude Desktop Claude Code inloggen OAuth token npx uvx referentiebeheer
---
Een **connector** geeft het model gereedschap van een ander programma of een dienst, via het Model Context Protocol (MCP). Bijvoorbeeld GitHub, of bestanden buiten de werkmap.

## Een connector toevoegen
Open Instellingen → **Connectors**.
- **Voorbeeld toevoegen** biedt connectors die meteen werken:

| Voorbeeld | Wat het doet | Nodig |
|---|---|---|
| Bestanden | lezen, zoeken en schrijven in Documenten, Downloads en Bureaublad | `npx` (winget install OpenJS.NodeJS.LTS) |
| Web ophalen | een webpagina als tekst ophalen | `uvx` (winget install astral-sh.uv) |
| GitHub | repositories, issues en pull requests | een persoonlijk token van GitHub |

- **Toevoegen…** voor je eigen connector: een *programma op deze pc* (commando en argumenten, zoals bij Claude Desktop) of een *internetadres*. Een programma dat geen .exe is, zoals `npx`, start de app via de opdrachtprompt.
- **Importeer** neemt connectors over uit Claude Desktop en Claude Code. Ze komen uitgeschakeld binnen, zodat je eerst kunt kijken.
- **Test verbinding** start de connector even en laat zien welke tools hij heeft.

De connectors staan in `mcp.json` in de gegevensmap van de app, in hetzelfde formaat als bij Claude Desktop.

## Zo gebruikt het model ze
Het model ziet alleen de namen van de connectors die aan staan. Pas als het er een nodig heeft, laadt het de tools ervan ("Connector laden: GitHub"). Die blijven daarna in dat gesprek, ook na een herstart van de app. Zo blijft de prompt kort en het model snel. Een connector met **Altijd geladen** is er in elk gesprek.

## Rechten
Per connector kies je **Toestaan**, **Eerst vragen** of **Weigeren**.
- Een tool die zegt dat hij alleen leest, mag standaard (behalve bij Weigeren).
- Andere tools vragen eerst. De kaart toont de connector, de tool en precies wat er wordt meegegeven.
- Een rode waarschuwing staat erbij als een tool iets kan verwijderen, als de connector op internet staat, of als de aanroep volgt op het lezen van het web. Na het lezen van het web vraagt ook Toestaan eerst.

## Inloggen bij een dienst
Vraagt een connector op internet om in te loggen, dan opent de app de inlogpagina in je eigen browser. Daarna gaat het vanzelf verder. Het token komt in **Windows Referentiebeheer**; WinAIMagic ziet je wachtwoord niet. **Uitloggen** staat in het menu van de connector. Voor een dienst zonder inlogpagina (zoals GitHub) vul je een token in; ook dat komt in Referentiebeheer.

## Agenda en Herinneringen
MacAIMagic heeft een ingebouwde connector voor de agenda en herinneringen van macOS. WinAIMagic heeft die niet. Een connector voor Outlook kun je zelf toevoegen als je er een hebt.

> Een connector op internet stuurt wat het model meegeeft naar die dienst. Kijk bij Eerst vragen goed naar de kaart.

## Iets werkt niet
Open in het menu van de connector **Log**: daar staat wat het programma of de dienst meldde. Ontbreekt `npx` of `uvx`, dan zegt de app hoe je het installeert.
