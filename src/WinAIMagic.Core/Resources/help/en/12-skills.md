---
title: Skills
icon: Lightbulb
keywords: skills instructions SKILL.md own subject
---
A **skill** is a short manual for the model about one subject, with commands that were tested on Windows 11. The model only sees the names and loads a skill when it needs it. That keeps the prompt short and the model fast.

## On or off
Skills are on by default. Turn them off under Settings → **System management** → **Skills**.

## Built-in skills
| Skill | About |
|---|---|
| uiterlijk | dark mode, accent colour, taskbar, File Explorer, file extensions |
| energie | sleep, display off, power plan, battery report |
| netwerk | adapters, DNS, Wi-Fi, testing a connection, flushing the DNS cache |
| software | finding, installing, updating and removing programs with winget |
| opstartitems | what starts with Windows, scheduled tasks, services |
| opslag | what takes space, cleaning up safely through the Recycle Bin |
| privacy-beveiliging | firewall, Defender, BitLocker, User Account Control |
| diagnose | slow, processor and memory, errors in the event log, drives |

## Making your own skill
The easiest way: ask in the chat, for example "make a skill for programming in C64 assembler, with the files in this folder". The model writes the skill, you see it on a card, and after **Allow** it is in the list. That is also how you change your own skill later.

You can also make one yourself:
1. Settings → System management → **Open folder**.
2. Make a folder, for example `my-printer`, with a file `SKILL.md` in it:
```
---
name: my-printer
description: Install and repair the office printer
platform: windows
---
Write the steps and commands here.
```
3. Your own skill with the same name as a built-in one replaces the built-in one.
4. A skill from MacAIMagic (`platform: macos`) can go into the folder as is, with its folder and scripts: it loads. The model sees which folder its scripts are in, and that its commands were written for macOS.
