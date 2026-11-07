---
title: Agent: e-mail, browser, programma's en spraak
icon: Robot
keywords: agent e-mail mail outlook concept versturen browser website surfen formulier klikken programma besturen ui automation spraak microfoon inspreken voorlezen stem dictee
---
Onder **Instellingen → Agent** laat je het model ook buiten de chat werken. Alles staat standaard uit, en elke wijziging komt eerst op een kaart.

## E-mail
Zet **E-mail via Outlook** aan. Het model werkt met de accounts die al in klassiek Outlook staan; WinAIMagic krijgt geen wachtwoord.
- **Lezen, zoeken en samenvatten** gebeurt zonder te vragen. Bijvoorbeeld: "Wat is er vandaag binnengekomen?" of "Zoek de factuur van de garage".
- **Een concept** opent in Outlook nadat je de kaart hebt goedgekeurd. Je verstuurt het zelf.
- **Versturen** vraagt altijd, ook als je eerder "Altijd in dit gesprek" koos. Kijk de ontvangers en de tekst na.
- Dit werkt met **klassiek Outlook**. De nieuwe Outlook en de Mail-app hebben geen koppeling voor andere programma's. Zonder klassiek Outlook kan het model alleen een concept klaarzetten in je standaard mailprogramma.

Tekst in een e-mail is voor het model informatie, geen opdracht.

## Browser
Zet **Browser in een eigen venster** aan. Het model opent pagina's in een eigen venster van WinAIMagic, zodat je kunt meekijken. Dat venster heeft eigen cookies, los van Edge en Chrome.
- Het model leest de pagina als tekst, met genummerde links, knoppen en velden.
- Een **link** volgen en in een **zoekveld typen** gebeuren zonder vragen.
- Een **knop** vraagt eerst toestemming.
- Een knop die iets **verstuurt, koopt, plaatst of verwijdert** vraagt elke keer, met een waarschuwing. Dat geldt ook voor het versturen van een formulier.
- **Inloggen doe je zelf** in het browservenster.
- **Wachtwoorden, pincodes en betaalgegevens** vult het model nooit in.

## Programma's besturen
Zet **Het model mag deze programma's besturen** aan en voeg met **Programma toevoegen…** de programma's toe die het mag gebruiken, bijvoorbeeld Kladblok of Rekenmachine: je kiest hun .exe.
- Het model ziet in het venster van dat programma de knoppen, velden en menu's, met nummers (via UI Automation van Windows). Het maakt geen schermafbeelding, dus het werkt ook met een model zonder beeld.
- Elke klik, elke tekst en elke toets komt op een kaart.
- Een knop die iets verstuurt of verwijdert, en de Enter-toets, vragen elke keer.
- **Nooit toegestaan**:
  - wachtwoordvelden;
  - opdrachtvensters, PowerShell en Terminal, de Register-editor, Windows-instellingen en het Configuratiescherm, Taakbeheer, wachtwoordmanagers en WinAIMagic zelf;
  - een programma sluiten met Alt+F4, en de Windows-toets.

## Spraak
Zet **Spraak** aan.
- **Voorlezen**: de luidspreker onder een antwoord leest het voor, met de stemmen van Windows, op deze pc. Met **Lees antwoorden vanzelf voor** gebeurt dat na elk antwoord in het venster.
- **Stem**: kies een stem in de taal van de app. Meer stemmen komen met een taal in Windows Instellingen → Tijd en taal → Spraak.
- **Inspreken**: druk op de microfoon naast het invoerveld en spreek je vraag in. Na twee seconden stilte gaat je vraag vanzelf weg; staat het model nog niet aan, dan laadt het eerst. Inspreken gebruikt **Windows-dictee**, en dat gaat via de online spraakdienst van Microsoft. Het werkt pas als je in Windows Instellingen → Privacy en beveiliging → Spraak de online spraakherkenning aanzet, en de microfoon toegang heeft.
- **Telegram**: spraakberichten kan WinAIMagic op Windows nog niet verstaan.

Een getypte vraag terwijl het model niet geladen is, laadt het model ook eerst; de vraag gaat daarna vanzelf.
