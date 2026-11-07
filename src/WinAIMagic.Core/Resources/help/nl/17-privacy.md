---
title: Privacy en veiligheid
icon: Lock
keywords: privacy veiligheid lokaal cloud gegevens wachtwoord sleutel prompt-injectie UAC
---
- **Alles lokaal**: het model draait op deze pc. Je gesprekken, bestanden en documenten gaan niet naar een AI-dienst.
- **Wat wel naar internet gaat**: zoekvragen en webpagina's (alleen als **Web** aan staat), het downloaden van modellen, en wat jij zelf deelt met **Deel op GitHub**.
- **Rechten**: in een werkmap, en bij systeembeheer, bepaal jij wat het model zonder te vragen mag.
- **Wachtwoorden en sleutels**: de app vraagt nooit om je wachtwoord. Zijn beheerdersrechten nodig, dan vraagt Windows zelf om toestemming (UAC). API-sleutels staan versleuteld in de instellingen.
- **Prompt-injectie**: een webpagina of bestand kan tekst bevatten die het model probeert te sturen. Daarom keur je wijzigingen zelf goed, en geeft de app een waarschuwing als een plan volgt op het lezen van het web.
- **Netwerk**: de API en cluster-helpers luisteren standaard alleen op deze pc of op de directe kabel.
