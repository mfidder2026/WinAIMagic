---
title: Chat
icon: Chat
keywords: conversation question thinking temperature system prompt context summary go on image paste copy stop new conversation delete trash
---
## Asking a question
Type your question at the bottom and press **Enter**. **Shift+Enter** starts a new line. The answer appears while the model writes; **Stop** breaks it off.

When no model runs yet, your question starts it by itself. Another model starts with one click: click the button with the model's name at the top and choose it, or press **Start this model** on its card under Models.

## Managing conversations
- **New conversation**: **New conversation** at the top of the conversations in the sidebar, the pencil at the top, or Ctrl+N. There is no maximum; the list scrolls.
- **Delete**: move over a conversation and click the trash can, or select it and press Delete (also with a right-click). The app asks first. Stop a conversation that is busy first.

## Buttons below the input field
| Button | What it does |
|---|---|
| Image | Add an image (also drag, or paste with Ctrl+V). Works when *vision* is on. |
| Think | Off, low, normal or high: how long the model thinks first (low 2,000 tokens, normal 8,000, high what Settings says). Works with every model that can think. Longer is better for hard questions, but slower. When the model works in steps, it thinks at most 4,000 tokens per step after the first. |
| Web | The model may search the web and read pages. |
| Work folder | Choose a folder the model may work in. |
| R W X | The rights in that folder: read, write, execute. |

## Context and summarizing
At the top of the conversation a meter shows how much of the context the conversation uses. At 80% the model summarizes the start; the messages stay visible. **Summarize now** in the meter does it at once.

## An answer in steps
When the model works in steps (files, commands, the web), the whole answer is in one card: every thought, text and step where it came. A thought is folded, with its start on one line; while the model thinks, you see the last lines of what it thinks now. While it works you see the latest 14 parts, with **earlier steps** for the rest. When the answer is done, all the work before the last text folds into one line **Reasoning and N steps**.

## When the model stops before it is done
Local models sometimes stop halfway. The app notices and tells the model to go on; you see that in the conversation. If it stops three times in a row, the app asks you to say "go on" or to give a hint.

## Settings for answers
Under **Settings → Chat**: thinking level, how long the model may think at most (16,000 tokens by default), temperature (higher is more creative) and your own system prompt.

> General notes the model should know in every conversation go in WINAIMAGIC.md: Settings → Chat → **Open file**.

Conversations are kept on this PC. Delete one with a right click in the sidebar.
