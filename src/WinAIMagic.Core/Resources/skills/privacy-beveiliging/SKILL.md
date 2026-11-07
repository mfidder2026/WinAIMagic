---
name: privacy-beveiliging
description: Beveiliging (firewall, Microsoft Defender, BitLocker, Gebruikersaccountbeheer, updates, SmartScreen)
platform: windows
---
Getest op Windows 11. Dit zijn de instellingen die de pc beschermen. Lees en leg uit; verander alleen wat de gebruiker
precies vraagt, en zeg wat het betekent. Uitzetten krijgt altijd een waarschuwing op de kaart.

Lezen:
- `Get-NetFirewallProfile`: per profiel (Domain, Private, Public) of de firewall aan staat (Enabled).
- `netsh advfirewall show allprofiles`: hetzelfde in tekst.
- `Get-MpComputerStatus`: Microsoft Defender, realtime bescherming en de datum van de definities.
- `Get-MpPreference`: de instellingen van Defender, ook uitzonderingen (ExclusionPath).
- `manage-bde -status`: BitLocker per schijf (beheerder; een weigering zonder beheerder is normaal).
- `Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System'`: Gebruikersaccountbeheer
  (EnableLUA 1 = aan, ConsentPromptBehaviorAdmin).
- `Get-HotFix`: geïnstalleerde updates met datum.
- `Get-ComputerInfo -Property OsName,OsVersion,OsBuildNumber`: de versie van Windows.

| Wat | check | apply | undo |
|---|---|---|---|
| Firewall aan | `Get-NetFirewallProfile` | `Set-NetFirewallProfile -Profile Domain,Public,Private -Enabled True` (admin) | de oude waarde per profiel (admin) |
| Defender-definities bijwerken | `Get-MpComputerStatus` | `Update-MpSignature` | niet nodig |
| Snelle scan | `Get-MpComputerStatus` | `Start-MpScan -ScanType QuickScan` | niet nodig |

Aanzetten mag; uitzetten (firewall, realtime bescherming, BitLocker, Gebruikersaccountbeheer, SmartScreen) doe je alleen
als de gebruiker dat uitdrukkelijk vraagt na jouw uitleg, met admin, en met een undo die het weer aanzet. Maak nooit een
uitzondering in Defender voor een map of programma dat je niet kent. BitLocker aan- of uitzetten duurt lang en vraagt de
herstelsleutel: laat de gebruiker dat zelf doen in `Start-Process ms-settings:deviceencryption`. Windows-beveiliging opent
met `Start-Process windowsdefender:`, Windows Update met `ms-settings:windowsupdate`, privacy met `ms-settings:privacy`.
