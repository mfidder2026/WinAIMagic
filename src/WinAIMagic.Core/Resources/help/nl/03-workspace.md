---
title: Werken in een map
icon: Folder
keywords: werkmap bestanden paneel broncode ontwikkelaar map bestanden schrijven lezen uitvoeren commando rechten LSU toestaan vragen weigeren WINAIMAGIC.md geleerd PowerShell
---
Geef het model een map, en het kan daar echt werken: bestanden lezen en schrijven, scripts uitvoeren, logbestanden lezen en net zo lang verbeteren tot het werkt. Commando's draaien in PowerShell.

## Een werkmap kiezen
Klik in de chat op **Werkmap** en kies een map. De map geldt voor dat gesprek.

## De bestanden ernaast
De knop **</>** naast de werkmap zet rechts van de chat een paneel aan met de bestanden van de werkmap. Klik een map open, of klik een bestand om de tekst te lezen. Wat het model schrijft, staat er binnen een seconde in, ook in het bestand dat open staat. Het paneel leest alleen; sleep de linkerrand om het breder te maken.

## Rechten: L S U
Naast de werkmapknop staan drie letters. Per soort actie kies je **Toestaan**, **Eerst vragen** of **Weigeren**:
- **L – Lezen**: mappen bekijken en bestanden lezen (standaard: toestaan)
- **S – Schrijven**: bestanden maken en aanpassen (standaard: eerst vragen)
- **U – Uitvoeren**: commando's, scripts en apps starten (standaard: eerst vragen)

Bij "eerst vragen" verschijnt een kaart met precies wat het model wil doen. Kies **Toestaan**, **Weigeren** of **Altijd in dit gesprek**. Enter is Toestaan, Esc is Weigeren.

> Het model kan alleen binnen de werkmap schrijven. Een koppeling naar buiten de map werkt niet.

## WINAIMAGIC.md: notities en wat het model leert
- Zet in de werkmap een bestand **WINAIMAGIC.md** met wat het model over die map moet weten, zoals hoe je een project bouwt. Het wordt bij elk bericht opnieuw gelezen.
- Na een taak van een paar stappen schrijft het model onder *Geleerd* wat werkte en wat niet, zodat het die fout niet nog eens maakt. Dat kun je uitzetten onder Instellingen → Chat → Werkmap.

## Goed om te weten
- Een commando dat te lang duurt, wordt na de time-out gestopt, met alles wat het startte. Programma's met een venster blijven open.
- Commando's in de werkmap krijgen geen beheerdersrechten. Voor instellingen van Windows is er *Systeembeheer*.
- Zonder ontwikkeltools (zoals Python of Git) zegt de app het model dat ze ontbreken.
