---
title: Lokale API
icon: Code
keywords: API OpenAI Anthropic Claude Code curl poort 8080 sleutel server netwerk
---
Terwijl de engine draait, kunnen andere programma's het model gebruiken op `http://127.0.0.1:8080`:
- **OpenAI**: `/v1/chat/completions` (en `/v1/responses`)
- **Anthropic**: `/v1/messages`

## Voorbeelden
Claude Code met het lokale model, in PowerShell:
```
$env:ANTHROPIC_BASE_URL = "http://127.0.0.1:8080"; claude
```
Elk OpenAI-compatibel programma:
```
curl.exe http://127.0.0.1:8080/v1/chat/completions -H "Content-Type: application/json" -d "{\"messages\":[{\"role\":\"user\",\"content\":\"Hallo!\"}]}"
```

## Vanaf een andere computer
Standaard luistert de API alleen op deze pc. Zet onder Instellingen → Engine het adres op `0.0.0.0` **en** stel een API-sleutel in. Herstart daarna de engine. Windows kan dan vragen of de app door de firewall mag.
