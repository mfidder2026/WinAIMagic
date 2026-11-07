---
title: Privacy and safety
icon: Lock
keywords: privacy safety local cloud data password key prompt injection UAC
---
- **Everything local**: the model runs on this PC. Your conversations, files and documents do not go to an AI service.
- **What does go to the internet**: search queries and web pages (only when **Web** is on), downloading models, and what you share yourself with **Share on GitHub**.
- **Rights**: in a work folder, and for system management, you decide what the model may do without asking.
- **Passwords and keys**: the app never asks for your password. When administrator rights are needed, Windows itself asks for permission (UAC). API keys are stored encrypted in the settings.
- **Prompt injection**: a web page or file can contain text that tries to steer the model. That is why you approve changes yourself, and the app warns when a plan follows reading the web.
- **Network**: the API and cluster helpers only listen on this PC or on the direct cable by default.
- **Connectors**: a connector on the internet sends what the model passes along to that service; one on this PC does not. Tokens are in the Windows Credential Manager.
- **Tasks**: a task or heartbeat works with the same rights as a conversation. A change always waits for your approval, also when you are not there.
- **Telegram**: your messages and the answers go through Telegram's servers, not end-to-end encrypted. Only paired accounts get an answer; the token is in the Windows Credential Manager. The model and your files stay on the PC.
- **Agent**: e-mail, the browser, programs and speech are off by default. Mail stays in Outlook; the browser window has its own cookies; reading aloud happens on this PC, but speaking goes through Microsoft's online speech service (Windows dictation). Sending, buying and deleting ask every time.
