---
title: System
icon: Info
keywords: system check graphics card engine page file Defender disk space NVMe recommendation
---
**System** checks at every start what WinAIMagic needs:
- the app's version, the graphics card, the RAM and which engine backend fits (CUDA, Vulkan or the processor);
- whether the engines start, and whether the NVIDIA pack is there in the portable version;
- the **page file**: the engine needs a large one to lock the model in memory;
- the drive of the model folder, and whether it is a fast NVMe drive;
- whether Windows Defender blocks the engine.

What the app can fix itself, it fixes. For the rest there is a button, such as **Set up** for the page file (it asks Windows for permission and counts after a restart). **Check again** runs the check once more. You also see which model suits this PC best.
