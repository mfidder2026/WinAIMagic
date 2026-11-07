---
title: Systeembeheer
icon: Shield
keywords: systeem instellen configureren Windows beheerder wachtwoord UAC admin plan goedkeuren ongedaan herstelpunt waarschuwing firewall taakbalk slaapstand diagnose register
---
Met systeembeheer vraag je het model om deze pc te bekijken of in te stellen, in gewone taal:
- "Waarom is mijn pc zo traag?"
- "Zet de taakbalk links en laat bestandsextensies zien"
- "Zet het scherm na 30 minuten uit"
- "Installeer VLC"

## Aan of uit
Systeembeheer staat standaard aan. Uitzetten kan onder Instellingen → **Systeembeheer** → **Het model mag deze pc bekijken en instellen**; het model krijgt dan geen gereedschap meer voor deze pc.

## Bekijken en wijzigen
- **Bekijken** (diagnose): het model leest instellingen met één PowerShell-commando tegelijk uit een vaste lijst die niets kan veranderen. Standaard mag dat zonder te vragen.
- **Wijzigen**: het model maakt een **plan**. Je ziet elke stap, met de waarde die er nu staat. Pas na **Toestaan** voert de app het plan uit. Daarna leest de app elke waarde opnieuw, ter controle. Een stap die mislukt, stopt het plan.

Beide rechten stel je in onder Instellingen → Systeembeheer: **Toestaan**, **Eerst vragen** of **Weigeren**.

## Beheerdersrechten
Sommige instellingen, zoals DNS, de firewall en diensten, hebben beheerdersrechten nodig. Zo'n plan heeft een 🔑 en het label **Beheerder**:
- het vraagt **altijd** eerst toestemming, ook als je "altijd in dit gesprek" hebt gekozen;
- daarna vraagt **Windows zelf** om toestemming (UAC), één keer voor alle beheerdersstappen. Er is geen wachtwoord dat WinAIMagic of het model ziet;
- het vinkje **Maak eerst een herstelpunt** maakt vooraf een herstelpunt van Windows. Standaard staat het aan. Windows maakt er hoogstens één per 24 uur.

## Waarschuwingen
Gevoelige wijzigingen worden niet geblokkeerd, maar krijgen een **rode waarschuwing** met wat ze betekenen. Bijvoorbeeld: de firewall, Defender of SmartScreen uitzetten, BitLocker, Gebruikersaccountbeheer, gebruikersaccounts, bestanden definitief verwijderen, de opstartinstellingen, of een herstart. Een plan dat volgt op het lezen van het web krijgt ook een waarschuwing.

## Ongedaan maken
- Vraag het model: "maak de laatste wijziging ongedaan". Het weet welke wijziging het was, en je keurt het terugzetten eerst goed op een kaart.
- De app houdt elke wijziging op de achtergrond bij, met de waarden van vóór de wijziging. Ook in een later gesprek kan het model een wijziging dus terugzetten.
- Van elke registersleutel die een stap wijzigt, bewaart de app vooraf een exacte kopie. Zo kan hij hem ook terugzetten als de ongedaanmaking van het model niet klopt.
- Daarna controleert de app of alles weer is zoals ervoor, en zegt hij het als dat niet zo is.

## Wat niet kan
Sommige dingen regelt Windows niet goed via commando's, zoals privacyrechten (camera, microfoon) of BitLocker aanzetten. Het model opent dan het juiste scherm van Instellingen en zegt wat je moet aanklikken.

> Begin met diagnose: dat leest alleen en verandert niets. Kijk bij een wijziging altijd naar de kaart voordat je op Toestaan drukt.
