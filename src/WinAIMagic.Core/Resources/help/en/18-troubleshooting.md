---
title: Troubleshooting
icon: Repair
keywords: problem error does not work slow will not start stuck memory VRAM download failed Defender UAC log
---
## The engine does not start
- Open **System** and look at what is orange; usually there is a button next to it.
- When Windows Defender blocks the engine, **Open Windows Security** opens the right page.
- Close large programs and games, and choose a smaller model when needed.

## Writing is slow
- Look under **Live** whether a lot is read from the SSD, and whether the VRAM is full. Then the model does not fit entirely: choose a compact model or close other programs.
- A page file that is too small makes the engine much slower: **System** says so and offers **Set up**.
- Turn **Think** off for simple questions.
- A long context makes prompt reading slower. Start a new conversation, or let the conversation be summarized.

## The model stops halfway or repeats itself
Say "go on", or give a hint. For work in a folder a short note in WINAIMAGIC.md helps.

## A download fails
Press **Download** again: the download goes on where it was. When the card says there is a newer version, use **Update**.

## A system change did not work
- The model says afterwards which step failed; it is also shown with the steps in the conversation.
- Ask the model to undo the change: the steps that did run go back.
- When you answered No to the Windows prompt (UAC), nothing was changed.

## The Windows prompt (UAC) does not appear
It can be behind the WinAIMagic window. Look in the taskbar, or press Alt+Tab.

## Still stuck?
Open **Compare → Export measurement**, or look in `%LOCALAPPDATA%\WinAIMagic\engine.log`.
