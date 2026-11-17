---
title: Getting started
icon: Home
keywords: begin first start install model download engine start usb portable stick work laptop
---
WinAIMagic runs large AI models entirely on this PC. Nothing goes to the cloud: your questions, files and answers stay here.

## In four steps
1. **System** checks at every start what the app needs. When something is orange, there is a button next to it that fixes it.
2. Choose a model under **Models**. Each card says whether it fits in your graphics card's VRAM plus the RAM, or partly streams from the SSD. The recommended model is the best one for this PC.
3. Press **Download**. A download resumes after an interruption, and every file is checked.
4. Press **Start** and open a **New conversation** (Ctrl+N).

> Close large programs and games before you start a large model: the engine wants the VRAM and as much RAM as it can get.

## What can you do with it?
- **Chat**, with thinking, images and web search (see *Chat* and *Web search*).
- **Work in a folder**: the model reads and writes files and runs commands (see *Working in a folder*).
- **Make images** under **Beeld**, and **Word documents** under **Documents**.
- **Compare models**, link several PCs in a **Cluster**, and use the model from other programs (see *Local API*).
- **Set up this PC**: the model looks at this PC and sets it up for you, with skills per subject (see *System management*).

## Just ask the app
The model knows WinAIMagic. Ask in the chat for what you want, and it does it, after your approval on a card:
- "Which model suits this PC best? Download it."
- "Turn thinking off and make the context 128K."
- "Make Ministral the fast model."
- "Make a skill for programming in C64 assembler."
- "Make an image of a lighthouse at sunset."

For that it reads the help itself and looks at how the app and this PC stand. It does not change its own rights, API keys or the server address: you set those yourself. Turn it off under Settings → System management → **The app**.

## Finding help
- At the top right is **?**: it opens the help for the screen that is open.
- **F1** does the same from any screen.
- Type a word in the search field, such as "administrator", "slow" or "taskbar".

## From a USB drive (Portable)
**WinAIMagic Portable** is a separate download (a zip) that runs from a USB drive without installing and without administrator rights. Unzip it, put the folder **WinAIMagic Portable** on the drive and open **WinAIMagic.exe** there.
- Everything goes into the folder **WinAIMagic-data** next to the app: models, conversations, the work folder, documents, pictures and the settings. On the PC itself it writes nothing in AppData, your own folders or the registry.
- It starts offline: web search, the browser, mail, controlling programs, Telegram, the heartbeat and the cluster network are off until you turn them on.
- It uses port 8090, so it can run beside an installed WinAIMagic. At the top left it says **USB · E:** in green; orange when the drive could not be written and the data is on this PC.
- The programs are not signed: if your organization blocks unsigned programs from a USB drive, it does not start there.
