---
title: Settings
icon: Settings
keywords: settings memory keep conversation language preferences context vision MTP port address VRAM reserve page file backend system management skills connectors tasks heartbeat notification area start with windows
---
Open the settings with the gear at the top right.

## Chat
- **Language**: Dutch, English, German or French. The model gets its instructions in the same language. The help is in Dutch and English.
- **Answers**: thinking level, maximum thinking, temperature, system prompt, and general notes (WINAIMAGIC.md).
- **Work folder**: working in a folder on/off, remembering what the model learns, and the rights R W X.
- **Web search**: your own Brave Search key.

## Engine
- **Context**: from 32K to 1M tokens. More context takes more memory.
- **Images (vision)**, **MTP**, **Skip experts** (GLM), **Start the engine when the app opens**, **Moving background**.
- **Keep conversations in the engine** (on by default): what the engine has read of a conversation goes to the SSD as soon as another conversation, a task or Documents gets the model, after every answer, and when the engine stops. When you go on later, also after a restart, the model does not read that conversation again. With a large model that saves minutes. It takes at most 40 GB (and never more than a tenth of the free disk); the oldest go first, and a deleted conversation takes its own along. **Clear** frees the space at once. Not on a cluster.
- **Graphics card**: the VRAM reserve, the page file and the engine backend.
- **Server**: address, port and API key (see *Local API*).

## System management
**Skills** and **This PC**: whether the model may look at this PC and set it up, and the rights to read and change the system. Both are on by default. The ? in that tab opens *System management*.

## Connectors
Tools from other programs and services, with rights per connector (see *Connectors*).

## Tasks
The **heartbeat** (on or off, how often, between which hours, and whether the model may load for it), **In the background** (stay in the notification area, stop the model when the window closes, start with Windows) and whether the model stops again after a task (see *Tasks and heartbeat*).
