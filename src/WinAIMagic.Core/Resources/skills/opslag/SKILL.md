---
name: opslag
description: Opslag en schijfruimte (vrije ruimte, grote mappen en bestanden, Prullenbak, tijdelijke bestanden, Opslaginzicht)
platform: windows
---
Getest op Windows 11. Lezen kan zonder beheerdersrechten. Opruimen in de eigen gebruikersmap kan zonder;
in C:\Windows of C:\Program Files niet, en dat hoort ook niet.

Lezen:
- `Get-PSDrive -PSProvider FileSystem`: vrije en gebruikte ruimte per schijf.
- `Get-Volume`: schijven met bestandssysteem en grootte.
- `Get-ChildItem -Path $HOME -Directory` werkt niet in system_read (geen `$`): schrijf het pad uit, bijvoorbeeld
  `Get-ChildItem -Path 'C:\Users\<naam>\Downloads' -File`.
- `Get-ChildItem -Path 'C:\Users\<naam>\Downloads' -File -Recurse -ErrorAction SilentlyContinue`: alle bestanden; tel en
  sorteer de grootte (Length) zelf.
- `Get-ChildItem -Path 'C:\Users\<naam>\AppData\Local\Temp' -File -Recurse -ErrorAction SilentlyContinue`: tijdelijke bestanden.

Opruimen gaat altijd naar de Prullenbak, zodat de gebruiker het terug kan halen:

| Wat | check | apply | undo |
|---|---|---|---|
| Bestand naar de Prullenbak | `Test-Path '<pad>'` | `Add-Type -AssemblyName Microsoft.VisualBasic; [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile('<pad>', 'OnlyErrorDialogs', 'SendToRecycleBin')` | zeg dat de gebruiker het in de Prullenbak terugzet (Terugzetten) |
| Map naar de Prullenbak | `Test-Path '<pad>'` | `Add-Type -AssemblyName Microsoft.VisualBasic; [Microsoft.VisualBasic.FileIO.FileSystem]::DeleteDirectory('<pad>', 'OnlyErrorDialogs', 'SendToRecycleBin')` | idem |
| Prullenbak legen | — | `Clear-RecycleBin -Force` | niet ongedaan te maken: vraag het eerst uitdrukkelijk |

`Remove-Item` verwijdert definitief, niet via de Prullenbak: gebruik het niet voor bestanden van de gebruiker. De kaart
waarschuwt bij `Remove-Item -Recurse`. Verwijder nooit iets in C:\Windows, C:\Program Files of andermans gebruikersmap, en
nooit modellen van WinAIMagic (die verwijdert de gebruiker op hun kaart). Noem eerst wat je wilt opruimen met de grootte,
en laat de gebruiker kiezen. Windows ruimt zelf op met Opslaginzicht: `Start-Process ms-settings:storagesense`;
schijfopruiming met `cleanmgr` laat de gebruiker zelf kiezen.
