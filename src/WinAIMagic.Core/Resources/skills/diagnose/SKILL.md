---
name: diagnose
description: Diagnose (wat de pc traag maakt, processor en geheugen, fouten in het logboek, schijven, opstarttijd, systeeminfo)
platform: windows
---
Getest op Windows 11. Alleen lezen: diagnose verandert niets. Begin breed, kijk dan gericht, en noem wat je zag met getallen.

Overzicht:
- `Get-ComputerInfo -Property OsName,OsVersion,CsProcessors,CsTotalPhysicalMemory,OsLastBootUpTime`: wat voor pc en sinds wanneer aan.
- `Get-CimInstance Win32_OperatingSystem`: vrij geheugen (FreePhysicalMemory, in KB) en laatste start.
- `Get-CimInstance Win32_Processor`: de processor en zijn belasting (LoadPercentage).
- `Get-CimInstance Win32_VideoController`: de videokaart en het stuurprogramma.
- `systeminfo`: alles in tekst, ook geïnstalleerde updates.

Wat de pc druk houdt:
- `Get-Process`: alle processen met CPU-tijd en geheugen (WS); sorteer zelf op CPU of WS en noem de grootste vijf.
- `Get-CimInstance Win32_PerfFormattedData_PerfOS_Processor -Filter "Name='_Total'"`: de belasting nu (PercentProcessorTime).
- `Get-CimInstance Win32_PerfFormattedData_PerfOS_Memory`: beschikbaar geheugen (AvailableMBytes).
- Gebruik geen `Get-Counter`: op een Windows in een andere taal dan Engels heten de tellers anders en faalt het.
- `Get-Service -Name <naam>`: of een dienst draait.

Fouten:
- `Get-WinEvent -LogName System -MaxEvents 50`: de laatste gebeurtenissen; kijk naar LevelDisplayName Fout en Kritiek.
- `Get-WinEvent -LogName Application -MaxEvents 50`: fouten van programma's.
- `Get-WinEvent -FilterHashtable` werkt niet in system_read (`@`): lees de laatste en filter zelf.

Schijven:
- `Get-PhysicalDisk`: type (SSD/HDD) en gezondheid (HealthStatus).
- `Get-PSDrive -PSProvider FileSystem`: vrije ruimte.

Een energierapport (`powercfg /energy /duration 60`) vraagt beheerdersrechten en duurt een minuut; vraag het eerst.
Herstel na diagnose (een dienst herstarten, een programma weghalen) gaat met system_change en de bijbehorende skill.
Gebruik Taakbeheer (`Start-Process taskmgr`) of Betrouwbaarheidscontrole (`Start-Process perfmon -ArgumentList '/rel'`)
als de gebruiker het zelf wil zien.
