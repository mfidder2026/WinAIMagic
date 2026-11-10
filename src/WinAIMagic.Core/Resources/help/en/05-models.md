---
title: Models
icon: Library
keywords: model download compact Q4 MTP memory VRAM RAM SSD stream update delete fast heavy start stop images model folder repeated text ngram
---
## Which model?
A large model does not have to fit in memory entirely here. Each card says for this PC:
- **Fits in VRAM + RAM**: everything is on the graphics card and in main memory. The fast choice.
- **Streams from SSD**: what does not fit comes from the SSD while the model writes. It works, but is slower; a fast NVMe drive helps.

**System** recommends the model that suits this PC best.

| Kind | What it means |
|---|---|
| **Compact** | Made smaller, so more of it fits in VRAM + RAM. |
| **Q4** | Closer to the original weights, but larger: the most used experts sit on the graphics card, the rest in RAM or on the SSD. |
| **MTP** | GLM and Qwen guess the next tokens ahead; the same answers, written faster. On/off under Settings → Engine. Below it, **Write repeated text faster**: text already in the conversation, such as code the model changes, is written in large pieces at once. |

## Two engines
The **main engine** divides a large model over VRAM, RAM and SSD. The **second engine** runs smaller models, the fast model, and the limited mode on a PC with little VRAM.

## Buttons on a card
- **Download**: resumes after an interruption, and every file is checked (sha256).
- **Check for update**: finds out in a few seconds whether there is a newer version, without reading the model. An update starts at once.
- **Update**: appears when there is a newer version; downloads only the files that differ.
- **Start this model**: loads the model to chat with; another model that runs stops first. While it runs, the button says **Stop**. At the top, the button with the model's name starts any downloaded model too, on every page.
- **Fast**: the model for small work in Documents and Beeld.
- **Images**: the model for Beeld.
- **Delete model**: frees the disk space (stop the model first).

## Model folder
By default the models are in `%LOCALAPPDATA%\WinAIMagic\models`, or the folder you chose in the setup. Choose another folder, also on another drive, under Models → Model folder → **Choose folder…**. The models move along, without downloading again.
