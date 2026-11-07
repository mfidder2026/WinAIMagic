---
title: Agent: e-mail, browser, programs and speech
icon: Robot
keywords: agent e-mail mail outlook draft send browser website browse form click program control ui automation speech microphone dictate read aloud voice dictation speech model whisper
---
Under **Settings → Agent** you let the model work outside the chat too. Everything is off by default, and every change comes on a card first.

## E-mail
Switch on **E-mail through Outlook**. The model works with the accounts that are already in classic Outlook; WinAIMagic gets no password.
- **Reading, searching and summarizing** happen without asking. For example: "What came in today?" or "Find the invoice from the garage".
- **A draft** opens in Outlook after you approve the card. You send it yourself.
- **Sending** always asks, also after "Always in this conversation". Check the recipients and the text.
- This works with **classic Outlook**. The new Outlook and the Mail app have no interface for other programs. Without classic Outlook the model can only prepare a draft in your default mail program.

Text in an e-mail is information for the model, not an instruction.

## Browser
Switch on **Browser in a window of its own**. The model opens pages in a window of WinAIMagic's own, so you can watch. That window has its own cookies, apart from Edge and Chrome.
- The model reads the page as text, with numbered links, buttons and fields.
- Following a **link** and **typing in a search field** happen without asking.
- A **button** asks first.
- A button that **sends, buys, posts or deletes** asks every time, with a warning. So does sending a form.
- **You log in yourself** in the browser window.
- **Passwords, PINs and payment details** the model never fills in.

## Controlling programs
Switch on **The model may control these programs** and add the programs it may use with **Add program…**, for example Notepad or Calculator: you choose their .exe.
- The model sees the buttons, fields and menus in that program's window, with numbers (through Windows UI Automation). It takes no screenshot, so it also works with a model without vision.
- Every click, every text and every key comes on a card.
- A button that sends or deletes, and the Enter key, ask every time.
- **Never allowed**:
  - password fields;
  - command windows, PowerShell and Terminal, the Registry Editor, Windows Settings and Control Panel, Task Manager, password managers and WinAIMagic itself;
  - closing a program with Alt+F4, and the Windows key.

## Speech
Switch on **Speech**.
- **Reading aloud**: the speaker under an answer reads it aloud with Windows' own voices, on this PC. With **Read answers aloud by themselves** that happens after every answer in the window.
- **Voice**: choose a voice in the app's language. More voices come with a language in Windows Settings → Time & language → Speech.
- **Speaking**: press the microphone next to the input field and say your question. After two seconds of silence it goes by itself; when the model is not loaded yet, it loads first. When you press stop yourself, the text goes into the input field and you can still change it.
- **Speech model**: speaking needs the speech model (Whisper, 0.6 GB). Download it under Settings → Agent → Speech, or with the button that appears the first time you press the microphone. The model understands you on this PC: what you say goes nowhere. With a graphics card a question takes a few seconds; without one, or when the loaded model fills the card, the processor does it and it takes a little longer.
- **Microphone**: when it gives only silence, check that desktop apps may use it in Windows Settings → Privacy & security → Microphone.
- **Telegram**: voice messages are understood by the same speech model, also on this PC.

A typed question while the model is not loaded also loads the model first; the question goes by itself afterwards.
