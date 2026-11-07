---
name: energie
description: Energie en slaapstand (scherm uit, slaapstand, energiebeheerschema, batterijrapport, wat de pc wakker houdt)
platform: windows
---
Getest op Windows 11. Lezen kan zonder beheerdersrechten. `powercfg /change` voor het eigen schema werkt zonder;
`/requests`, `/energy` en het wisselen van schema voor alle gebruikers vragen beheerdersrechten (`admin: true`).

Lezen:
- `powercfg /getactivescheme`: het schema dat nu geldt.
- `powercfg /list`: alle schema's met hun GUID.
- `powercfg /query`: alle instellingen van het actieve schema (lang; zoek SUB_VIDEO en SUB_SLEEP).
- `powercfg /availablesleepstates`: welke slaapstanden deze pc kent.
- `powercfg /lastwake` en `powercfg /waketimers`: waardoor de pc wakker werd of zal worden.
- `powercfg /requests`: wat de pc nu wakker houdt (beheerder; een weigering zonder beheerder is normaal).
- `powercfg /batteryreport /duration 7`: schrijft battery-report.html in de gebruikersmap; zeg waar het staat.

Wijzigen (minuten; 0 = nooit). `-ac` aan de lader, `-dc` op de batterij:

| Wat | check | apply | undo |
|---|---|---|---|
| Scherm uit | `powercfg /query` (SUB_VIDEO, VIDEOIDLE) | `powercfg /change monitor-timeout-ac 30` | `powercfg /change monitor-timeout-ac <oude waarde>` |
| Slaapstand | `powercfg /query` (SUB_SLEEP, STANDBYIDLE) | `powercfg /change standby-timeout-ac 0` | oude waarde |
| Sluimerstand na | `powercfg /query` (SUB_SLEEP, HIBERNATEIDLE) | `powercfg /change hibernate-timeout-dc 180` | oude waarde |
| Ander schema | `powercfg /getactivescheme` | `powercfg /setactive <GUID uit /list>` | `powercfg /setactive <oude GUID>` |

`powercfg /query` geeft seconden in hexadecimaal (0x00000708 = 1800 s = 30 minuten): reken om naar minuten voor de undo.
Schrijf de oude waarde uit de check in de undo; nooit een gok. `powercfg /hibernate off` zet de sluimerstand helemaal uit:
werk kan dan verloren gaan als de batterij leeg raakt. Doe dat alleen als de gebruiker precies dat vraagt (beheerder).

Batterijgezondheid staat in het batterijrapport (ontwerpcapaciteit tegenover volle lading). Schermhelderheid en
de energiestand kies je in `Start-Process ms-settings:powersleep` of `ms-settings:batterysaver`.
