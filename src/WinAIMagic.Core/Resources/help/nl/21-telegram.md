---
title: Telegram
icon: Send
keywords: telegram bot botfather koppelen koppelcode telefoon onderweg bericht berichten knop knoppen goedkeuren melding token
---
Via Telegram praat je met het model op deze pc, ook als je er niet bent. Dat kan vanaf je telefoon of een andere computer. Het model blijft op de pc; Telegram brengt alleen de berichten over.

## Instellen
1. Open Telegram, zoek **@BotFather** en stuur **/newbot**. Kies een naam en een gebruikersnaam die op `bot` eindigt.
2. BotFather geeft je een **token**. Plak die in WinAIMagic bij **Instellingen → Telegram → Bot** en klik op **Bewaar**. De token komt in Windows Referentiebeheer, niet in een bestand.
3. Zet **Telegram** aan. Bij Status staat dan "Verbonden als @jouwbot".
4. Klik op **Koppel een account**. Je krijgt een code van 6 cijfers. Stuur die code naar je bot. Dat moet binnen 10 minuten, in een privégesprek. De bot antwoordt met "Gekoppeld".

Alleen gekoppelde accounts krijgen antwoord. Op berichten van anderen reageert de bot niet: zo weten zij niet eens dat hij er is. Bij **Wie mag praten** ontkoppel je een account weer.

## Praten
- Elk gekoppeld account heeft één gesprek dat doorloopt: "✈︎ Telegram · naam". Het staat ook in de zijbalk, zodat je daar verder kunt lezen.
- Het antwoord groeit in Telegram mee terwijl het model schrijft. Een lang antwoord komt in meerdere berichten; een tabel wordt nette kolommen.
- **/nieuw** begint een nieuw gesprek.
- **/stop** stopt het antwoord.
- **/status** laat zien of het model geladen is, wat er loopt, wat de volgende taak is en hoeveel ruimte er vrij is.
- Is het model niet geladen, dan laadt het bij je vraag. De bot zegt "Model laden…"; bij een groot model duurt dat een paar minuten. Daarna blijft het model geladen.
- **Foto's** gaan naar het model als Afbeeldingen aan staat.
- **Tekstbestanden** tot 1 MB leest het model mee.
- **Spraakberichten** verstaat het spraakmodel op de pc (Instellingen → Agent → Spraak); je krijgt eerst te zien wat het hoorde.

## Goedkeuren
Wil het model iets wijzigen, zoals een bestand schrijven, een commando uitvoeren of een connector met schrijfrechten gebruiken? Dan krijg je in Telegram een kaart met **Toestaan**, **Weigeren** en **Altijd in dit gesprek**. Dat gebeurt volgens dezelfde rechten als in de chat op de pc.
- Alleen het account van dat gesprek kan op de knoppen drukken.
- Antwoord je binnen een dag niet, dan telt dat als nee.
- **Beheerdersrechten** keur je nooit via Telegram goed. De bot zegt dan dat je het op de pc moet doen, en je krijgt daar een melding.
- Wat elke keer vraagt (een e-mail versturen, een knop die iets koopt) heeft geen knop **Altijd in dit gesprek**.

## Taken en heartbeat
Staat **Taken en heartbeat ook naar Telegram** aan, dan gaat het volgende ook naar het account dat je als eerste koppelde:
- een taak die klaar is of stopte;
- wat de heartbeat vond;
- een goedkeuring die een taak nodig heeft (met knoppen).

Met Telegram aan blijft WinAIMagic rechtsonder in het systeemvak als je het venster sluit. Zo blijft de bot antwoorden. Slaapt de pc, dan komen de berichten pas als hij weer wakker is. Telegram bewaart ze tot een dag.

## Privacy
Je berichten en de antwoorden gaan via de servers van Telegram. Ze zijn daar niet versleuteld van begin tot eind, want een bot kan geen geheime chat gebruiken. Stuur dus niets via Telegram wat je niet aan Telegram zou toevertrouwen. Het model zelf, je bestanden en je instellingen blijven op de pc. De pc belt zelf naar Telegram; er staat niets open voor het internet.

## Problemen
- **"Telegram kent deze token niet"**: de token is fout of ingetrokken. Verwijder hem, maak bij BotFather een nieuwe aan (**/token**) en plak die.
- **"Een ander programma gebruikt deze bot al"**: een bot kan maar door één programma tegelijk gelezen worden. Stop het andere programma (bijvoorbeeld MacAIMagic met dezelfde bot), of maak een aparte bot voor WinAIMagic.
- **Geen antwoord**: kijk of Telegram aan staat, of de status "Verbonden" zegt en of je account bij Wie mag praten staat. Het log onderaan laat zien wat er gebeurde.
