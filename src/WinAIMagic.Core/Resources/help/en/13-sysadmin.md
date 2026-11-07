---
title: System management
icon: Shield
keywords: system set up configure Windows administrator admin password UAC plan approve undo restore point warning firewall taskbar sleep diagnosis registry
---
With system management you ask the model to look at this PC or set it up, in plain words:
- "Why is my PC so slow?"
- "Put the taskbar on the left and show file extensions"
- "Turn the display off after 30 minutes"
- "Install VLC"

## On or off
System management is on by default. Turn it off under Settings → **System management** → **The model may look at this PC and set it up**; the model then gets no tools for this PC.

## Looking and changing
- **Looking** (diagnosis): the model reads settings with one PowerShell command at a time, from a fixed list that cannot change anything. By default it may do that without asking.
- **Changing**: the model makes a **plan**. You see every step, with the value that is there now. Only after **Allow** does the app run the plan. Then the app reads every value again, as a check. A step that fails stops the plan.

Set both rights under Settings → System management: **Allow**, **Ask first** or **Deny**.

## Administrator rights
Some settings, such as DNS, the firewall and services, need administrator rights. Such a plan has a 🔑 and the label **Administrator**:
- it **always** asks first, also when you chose "always in this conversation";
- then **Windows itself** asks for permission (UAC), once for all administrator steps. There is no password that WinAIMagic or the model sees;
- the checkbox **Make a restore point first** makes a Windows restore point before. It is on by default. Windows makes at most one per 24 hours.

## Warnings
Sensitive changes are not blocked, but get a **red warning** with what they mean. For example: turning off the firewall, Defender or SmartScreen, BitLocker, User Account Control, user accounts, deleting files for good, the boot settings, or a restart. A plan that follows reading the web gets a warning too.

## Undo
- Ask the model: "undo the last change". It knows which change it was, and you approve putting it back on a card first.
- The app records every change in the background, with the values from before. So the model can put a change back in a later conversation too.
- Of every registry key a step changes, the app keeps an exact copy before. So it can put it back even when the model's undo is wrong.
- Then the app checks whether everything is as it was, and says so when it is not.

## What cannot be done
Some things Windows does not handle well through commands, such as privacy rights (camera, microphone) or turning on BitLocker. The model then opens the right page of Settings and says what to click.

> Start with diagnosis: it only reads and changes nothing. With a change, always look at the card before you press Allow.
