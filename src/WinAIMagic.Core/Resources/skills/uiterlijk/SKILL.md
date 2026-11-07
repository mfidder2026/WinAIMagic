---
name: uiterlijk
description: Uiterlijk, taakbalk en Verkenner (donkere modus, accentkleur, taakbalk links of midden, bestandsextensies, verborgen bestanden)
platform: windows
---
Getest op Windows 11. Lees met system_read, wijzig met system_change. Elke stap heeft een check, een apply en een undo.
Geen van deze instellingen heeft beheerdersrechten nodig: ze staan onder HKCU.

Een waarde die nooit gezet is, geeft bij het lezen een fout ("Property … does not exist"): dan geldt de standaard.
De undo is dan `Remove-ItemProperty -Path '<sleutel>' -Name <naam>`.
Taakbalk en Verkenner lezen hun instellingen bij het starten: zet `Stop-Process -Name explorer -Force` achter de apply
en achter de undo. Verkenner start vanzelf opnieuw; open vensters van de Verkenner gaan dicht, zeg dat.

| Wat | check | apply (voorbeeld) | undo (voorbeeld) |
|---|---|---|---|
| Donkere modus apps | `Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme` | `Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name AppsUseLightTheme -Value 0 -Type DWord` | dezelfde met de oude waarde (1 = licht) |
| Donkere modus Windows | `Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name SystemUsesLightTheme` | `… -Name SystemUsesLightTheme -Value 0 -Type DWord` | oude waarde |
| Taakbalk links of midden | `Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name TaskbarAl` | `Set-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name TaskbarAl -Value 0 -Type DWord; Stop-Process -Name explorer -Force` (0 links, 1 midden) | oude waarde, of `Remove-ItemProperty … -Name TaskbarAl`, en explorer herstarten |
| Bestandsextensies tonen | `Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name HideFileExt` | `… -Name HideFileExt -Value 0 -Type DWord; Stop-Process -Name explorer -Force` (0 tonen) | oude waarde, explorer herstarten |
| Verborgen bestanden tonen | `Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced' -Name Hidden` | `… -Name Hidden -Value 1 -Type DWord; Stop-Process -Name explorer -Force` (1 tonen, 2 verbergen) | oude waarde, explorer herstarten |
| Transparantie | `Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name EnableTransparency` | `… -Name EnableTransparency -Value 0 -Type DWord` | oude waarde |
| Accentkleur op taakbalk | `Get-ItemPropertyValue -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -Name ColorPrevalence` | `… -Name ColorPrevalence -Value 1 -Type DWord` | oude waarde |

Lees een hele sleutel in één keer met `Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced'`.
Gok nooit een waardenaam die je niet in die uitvoer ziet. Bureaubladachtergrond, thema's en de accentkleur zelf kies je
het best in Instellingen: `Start-Process ms-settings:personalization` (kleuren: `ms-settings:colors`, taakbalk:
`ms-settings:taskbar`). Zeg dan wat de gebruiker moet aanklikken.
