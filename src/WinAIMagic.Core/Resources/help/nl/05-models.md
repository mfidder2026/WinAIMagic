---
title: Modellen
icon: Library
keywords: model downloaden compact Q4 MTP geheugen VRAM RAM SSD streamen update verwijderen snel zwaar start starten stoppen afbeeldingen modelmap
---
## Welk model?
Een groot model hoeft hier niet helemaal in het geheugen te passen. Elke kaart zegt voor deze pc:
- **Past in VRAM + RAM**: alles staat op de videokaart en in het werkgeheugen. De snelle keuze.
- **Streamt van SSD**: wat niet past, komt tijdens het schrijven van de SSD. Dat werkt, maar is trager; een snelle NVMe-schijf helpt.

**Systeem** raadt het model aan dat het best bij deze pc past.

| Soort | Kenmerk |
|---|---|
| **Compact** | Kleiner gemaakt, zodat meer ervan in VRAM + RAM past. |
| **Q4** | Dichter bij de originele gewichten, maar groter: de meest gebruikte experts staan op de videokaart, de rest in het RAM of op de SSD. |
| **MTP** | GLM en Qwen raden de volgende tokens vooraf; dezelfde antwoorden, sneller schrijven. Aan/uit onder Instellingen → Engine. |

## Twee engines
De **hoofdengine** verdeelt een groot model over VRAM, RAM en SSD. De **tweede engine** draait kleinere modellen, het snelle model en de beperkte modus op een pc met weinig VRAM.

## Knoppen op een kaart
- **Download**: gaat verder na een onderbreking, en elk bestand wordt gecontroleerd (sha256).
- **Controleer op update**: kijkt in een paar seconden of er een nieuwere versie is, zonder het model te lezen. Een update begint meteen.
- **Update**: verschijnt als er een nieuwere versie is; downloadt alleen de bestanden die anders zijn.
- **Start dit model**: laadt het model om mee te chatten; een ander model dat draait, stopt eerst. Draait het, dan staat er **Stop**. Bovenaan start je met de knop met de modelnaam ook elk gedownload model, op elke pagina.
- **Snel**: het model voor klein werk in Documenten en Beeld.
- **Afbeeldingen**: het model voor Beeld.
- **Verwijder model**: maakt de schijfruimte vrij (stop het model eerst).

## Modelmap
Standaard staan de modellen in `%LOCALAPPDATA%\WinAIMagic\models`, of in de map die je in de setup koos. Een andere map, ook op een andere schijf, kies je onder Modellen → Modelmap → **Kies map…**. De modellen gaan mee, zonder opnieuw te downloaden.
