---
title: Local API
icon: Code
keywords: API OpenAI Anthropic Claude Code curl port 8080 key server network
---
While the engine runs, other programs can use the model at `http://127.0.0.1:8080`:
- **OpenAI**: `/v1/chat/completions` (and `/v1/responses`)
- **Anthropic**: `/v1/messages`

## Examples
Claude Code with the local model, in PowerShell:
```
$env:ANTHROPIC_BASE_URL = "http://127.0.0.1:8080"; claude
```
Any OpenAI-compatible program:
```
curl.exe http://127.0.0.1:8080/v1/chat/completions -H "Content-Type: application/json" -d "{\"messages\":[{\"role\":\"user\",\"content\":\"Hello!\"}]}"
```

## From another computer
By default the API only listens on this PC. Under Settings → Engine set the address to `0.0.0.0` **and** set an API key. Then restart the engine. Windows may ask whether the app may pass the firewall.
