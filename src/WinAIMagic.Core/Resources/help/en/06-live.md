---
title: Live dashboard
icon: Speed
keywords: speed tokens per second GPU CPU VRAM RAM memory SSD chart gauge
---
**Live** shows what the PC does while the model works:
- the **speed gauge**: tokens per second while writing;
- charts of the **graphics card** and the **processor**, the **VRAM**, the **RAM** and what is read from the **SSD**;
- a mini version at the bottom of the sidebar; click it to open Live.

## Two speeds
- **Writing** (decode): how fast the answer appears.
- **Prompt reading** (prefill): how fast the model reads your question, the attachments and the web pages. In long tasks this is often the bottleneck.

> When the model reads a lot from the SSD, it does not fit entirely in VRAM + RAM. Choose a compact model, close other programs, or turn on skipping experts for GLM (Settings → Engine).

A full graphics card gives no error on Windows, but the speed drops sharply. That is why the app keeps a VRAM reserve free; set it under Settings → Engine.
