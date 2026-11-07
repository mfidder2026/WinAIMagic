---
name: netwerk
description: Netwerk (adapters, IP-adressen, DNS-servers, wifi, verbinding testen, DNS-cache legen)
platform: windows
---
Getest op Windows 11. Lezen kan zonder beheerdersrechten; DNS-servers zetten vraagt beheerdersrechten (`admin: true`).

Lezen:
- `Get-NetAdapter`: de adapters, hun status en snelheid. Onthoud de naam (InterfaceAlias), bijvoorbeeld `Wi-Fi` of `Ethernet`.
- `Get-NetIPConfiguration`: per adapter het IP-adres, de gateway en de DNS-servers.
- `Get-DnsClientServerAddress -AddressFamily IPv4`: de DNS-servers per adapter.
- `ipconfig /all`: alles in één overzicht, ook DHCP.
- `Test-NetConnection 1.1.1.1` en `Test-NetConnection example.com -Port 443`: kan de pc erbij?
- `Resolve-DnsName example.com`: werkt DNS?
- `netsh wlan show interfaces`: het wifinetwerk, de signaalsterkte en de snelheid.
- `netsh wlan show profiles`: bekende wifinetwerken.
- `Get-NetConnectionProfile`: of een netwerk Openbaar of Privé is.

| Wat | check | apply | undo |
|---|---|---|---|
| DNS-servers | `Get-DnsClientServerAddress -InterfaceAlias 'Wi-Fi' -AddressFamily IPv4` | `Set-DnsClientServerAddress -InterfaceAlias 'Wi-Fi' -ServerAddresses 1.1.1.1,1.0.0.1` (admin) | `Set-DnsClientServerAddress -InterfaceAlias 'Wi-Fi' -ResetServerAddresses` als de check leeg was of van DHCP kwam, anders de oude adressen (admin) |
| Netwerk Privé of Openbaar | `Get-NetConnectionProfile` | `Set-NetConnectionProfile -InterfaceAlias 'Wi-Fi' -NetworkCategory Private` (admin) | de oude categorie (admin) |
| DNS-cache legen | — | `Clear-DnsClientCache` | niet nodig; zeg dat dit niets blijvends verandert |

Een openbaar netwerk is strenger beveiligd: zet een netwerk alleen op Privé als het een vertrouwd netwerk thuis of
op het werk is, en zeg dat. Een proxy wijzigen krijgt een waarschuwing op de kaart. Wifi-wachtwoorden lees je niet
voor tenzij de gebruiker daar zelf om vraagt. VPN en proxy kies je in `Start-Process ms-settings:network-vpn` of
`ms-settings:network-proxy`; wifi in `ms-settings:network-wifi`.
