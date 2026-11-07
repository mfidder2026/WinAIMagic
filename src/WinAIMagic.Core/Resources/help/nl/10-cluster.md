---
title: Cluster: meerdere pc's
icon: Network
keywords: cluster helper USB4 Thunderbolt ethernet kabel meerdere pc's geheugen RPC netwerk
---
Met meerdere pc's is er één **hoofd-pc** (daar chat je) en zijn de andere **helpers**, die een deel van het model op hun videokaart houden. Dit werkt voor modellen op de tweede engine.

## Instellen
1. Verbind elke helper met de hoofd-pc: een USB4- of Thunderbolt-kabel, of anders ethernet.
2. Op elke helper: **Cluster** → rol **Helper** → **Start helper**.
3. Op de hoofd-pc staan de helpers onder **Cluster**, met hun videokaart en geheugen. Vink ze aan en start een model.

## Of vraag het de app
- Zeg op elke andere pc tegen WinAIMagic: "word helper in het cluster". Na een kaart stopt de engine daar en start de helper.
- Zeg op de hoofd-pc: "zoek alle helpers, bouw het cluster en start het model". Het model zoekt de helpers, vinkt ze aan na een kaart en start de engine.

## Mac en Windows samen
Een Mac met MacAIMagic kan helper zijn voor een pc, en andersom: beide spreken hetzelfde clusterprotocol. Tussen een Mac en een pc is er meestal geen directe kabel: sta dan op beide het lokale netwerk toe. Dit is nog niet met echte machines getest.

## Laden duurt lang, of blijft hangen
- De eerste start stuurt elke helper zijn deel van het model. Over gigabit-LAN is dat ongeveer 100 MB/s, dus 20 GB duurt een paar minuten; over wifi veel langer. Onder **Samen** zie je hoeveel er al is gestuurd. De helper bewaart wat hij kreeg, dus een volgende start gaat veel sneller.
- Antwoordt een helper niet op zijn poort, dan start de engine zonder hem en zegt de app waarom. Meestal houdt de firewall op die pc de verbinding tegen, of staat het netwerk daar op Openbaar.
- Gaat er anderhalve minuut niets meer naar de helpers, dan zegt de app dat ook.

## Veiligheid
Een helper luistert alleen op de directe verbinding, tenzij je het lokale netwerk toestaat. De verbinding heeft geen wachtwoord: gebruik een directe kabel of een netwerk dat je vertrouwt.

> Windows kan de eerste keer vragen of de app door de firewall mag. Sta dat alleen toe voor een privénetwerk.
