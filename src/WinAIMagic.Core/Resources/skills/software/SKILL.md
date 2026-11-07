---
name: software
description: Programma's installeren, bijwerken en verwijderen met winget (zoeken, lijst, updates)
platform: windows
---
Getest op Windows 11 met winget. Lezen kan zonder beheerdersrechten. Installeren en bijwerken vraagt soms zelf om
toestemming van Windows; markeer een stap als admin als het programma voor alle gebruikers wordt geïnstalleerd.

Lezen:
- `winget search vlc`: zoek een programma; gebruik daarna het exacte Id.
- `winget show --id VideoLAN.VLC -e`: wat het is, de uitgever en de versie.
- `winget list`: wat er geïnstalleerd is (ook programma's die niet via winget kwamen).
- `winget list --id VideoLAN.VLC -e`: of één programma er is, en welke versie.
- `winget upgrade`: welke programma's een update hebben (zonder Id installeert dit niets).
- `Get-Package` werkt ook, maar `winget list` is vollediger.

| Wat | check | apply | undo |
|---|---|---|---|
| Installeren | `winget list --id <Id> -e` | `winget install --id <Id> -e --accept-package-agreements --accept-source-agreements --silent` | `winget uninstall --id <Id> -e --silent` |
| Bijwerken | `winget list --id <Id> -e` | `winget upgrade --id <Id> -e --accept-package-agreements --accept-source-agreements --silent` | er is geen terugweg; zeg dat (of installeer de oude versie met `--version <oud>`) |
| Verwijderen | `winget list --id <Id> -e` | `winget uninstall --id <Id> -e --silent` | `winget install --id <Id> -e --silent` (instellingen van het programma kunnen weg zijn) |

Gebruik altijd het exacte Id (`--id … -e`), nooit alleen een naam: een naam kan bij een ander programma horen.
Zeg welke uitgever het programma heeft voordat je het installeert. `winget upgrade --all` werkt alles tegelijk bij;
doe dat alleen als de gebruiker dat vraagt, en zeg dat open programma's dan kunnen sluiten.
Apps uit de Microsoft Store staan ook in `winget list` (bron msstore). Programma's en onderdelen van Windows zelf
beheer je in `Start-Process ms-settings:appsfeatures`; standaard-apps in `ms-settings:defaultapps`.
