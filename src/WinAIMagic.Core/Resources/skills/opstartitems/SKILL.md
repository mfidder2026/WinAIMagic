---
name: opstartitems
description: Wat er meestart met Windows (opstart-apps, Run-sleutels, geplande taken, diensten)
platform: windows
---
Getest op Windows 11. Lezen kan zonder beheerdersrechten. Wijzigen onder HKCU kan zonder; onder HKLM, bij diensten en
bij geplande taken van het systeem zijn beheerdersrechten nodig (`admin: true`).

Lezen:
- `Get-CimInstance Win32_StartupCommand`: alles wat bij het aanmelden start, met waar het vandaan komt.
- `Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'`: opstart-apps van deze gebruiker.
- `Get-ItemProperty -Path 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'`: voor alle gebruikers.
- `Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run'`: wat in
  Taakbeheer is uitgezet (de eerste byte 02 is aan, 03 is uit).
- `Get-ScheduledTask -TaskPath '\'`: geplande taken buiten die van Microsoft; `Get-ScheduledTask -TaskName <naam>` voor één.
- `Get-Service -Name <naam>` en `Get-CimInstance Win32_Service -Filter "StartMode='Auto'"`: diensten en hoe ze starten.

| Wat | check | apply | undo |
|---|---|---|---|
| Opstart-app weghalen (gebruiker) | `Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'` | `Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name '<naam>'` | `New-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name '<naam>' -Value '<oude opdracht>' -PropertyType String` |
| Geplande taak uit | `Get-ScheduledTask -TaskName '<naam>'` | `Disable-ScheduledTask -TaskName '<naam>'` | `Enable-ScheduledTask -TaskName '<naam>'` |
| Dienst niet automatisch | `Get-Service -Name '<naam>'` (StartType) | `Set-Service -Name '<naam>' -StartupType Manual` (admin) | `Set-Service -Name '<naam>' -StartupType <oude waarde>` (admin) |

Schrijf de oude opdracht precies over uit de check (aanhalingstekens en argumenten), anders werkt de undo niet.
Zet geen diensten van Windows zelf uit (Windows Update, Defender, de firewall, Gebeurtenislogboek): de kaart waarschuwt
daarvoor. Weet je niet wat iets is, zoek het eerst op en zeg wat je vond. Opstart-apps aan- en uitzetten kan de gebruiker
ook zelf in `Start-Process ms-settings:startupapps`.
