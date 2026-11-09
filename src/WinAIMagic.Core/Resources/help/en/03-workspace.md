---
title: Working in a folder
icon: Folder
keywords: work folder files panel source code developer folder files write read execute command rights allow ask deny WINAIMAGIC.md learned PowerShell
---
Give the model a folder, and it can really work there: read and write files, run scripts, read logs and keep improving until it works. Commands run in PowerShell.

## Choosing a work folder
Click **Work folder** in the chat and choose a folder. The folder applies to that conversation.

## The files alongside
The **</>** button next to the work folder turns on a panel to the right of the chat with the files of the work folder. Click a folder to open it, or a file to read its text. What the model writes shows up within a second, also in the file that is open. The panel only reads; drag its left edge to make it wider.

## Rights: R W X
Next to the folder button are three letters. For each kind of action you choose **Allow**, **Ask first** or **Deny**:
- **R – Read**: look at folders and read files (default: allow)
- **W – Write**: create and change files (default: ask first)
- **X – Execute**: run commands, scripts and apps (default: ask first)

With "ask first" a card shows exactly what the model wants to do. Choose **Allow**, **Deny** or **Always in this conversation**. Enter is Allow, Esc is Deny.

> The model can only write inside the work folder. A link pointing outside the folder does not work.

## WINAIMAGIC.md: notes and what the model learns
- Put a file **WINAIMAGIC.md** in the work folder with what the model should know about it, such as how to build the project. It is read again with every message.
- After a task of a few steps the model writes under *Learned* what worked and what did not, so it does not make that mistake again. You can turn that off under Settings → Chat → Work folder.

## Good to know
- A command that takes too long is stopped after the time-out, with everything it started. Programs with a window stay open.
- Commands in the work folder get no administrator rights. For Windows settings there is *System management*.
- Without developer tools (such as Python or Git) the app tells the model they are missing.
