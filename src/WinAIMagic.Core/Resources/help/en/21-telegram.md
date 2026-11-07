---
title: Telegram
icon: Send
keywords: telegram bot botfather pair pairing code phone away message messages button buttons approve notification token
---
Through Telegram you talk with the model on this PC, also when you are not there: from your phone or another computer. The model stays on the PC; Telegram only carries the messages.

## Setting up
1. Open Telegram, look for **@BotFather** and send **/newbot**. Pick a name and a username that ends in `bot`.
2. BotFather gives you a **token**. Paste it in WinAIMagic under **Settings → Telegram → Bot** and click **Save**. The token goes into Windows Credential Manager, not into a file.
3. Switch **Telegram** on. Status then says "Connected as @yourbot".
4. Click **Pair an account**. You get a 6-digit code. Send that code to your bot within 10 minutes, in a private chat. The bot answers "Paired".

Only paired accounts get an answer. The bot does not react to anyone else, so they cannot even tell it is there. Under **Who may talk** you unpair an account again.

## Talking
- Each paired account has one conversation that goes on: "✈︎ Telegram · name". It is in the sidebar too, so you can read on there.
- The answer grows in Telegram while the model writes. A long answer comes in several messages; a table becomes neat columns.
- **/new** starts a new conversation.
- **/stop** stops the answer.
- **/status** shows whether the model is loaded, what runs, the next task and the free disk space.
- When the model is not loaded, it loads for your question. The bot says "Loading the model…"; a large model takes a few minutes. Afterwards it stays loaded.
- **Photos** go to the model when Images is on.
- **Text files** up to 1 MB are read along.
- **Voice messages** are understood by the speech model on the PC (Settings → Agent → Speech); you first see what it heard.

## Approving
Does the model want to change something, such as writing a file, running a command or using a connector that writes? Then Telegram shows a card with **Allow**, **Deny** and **Always in this conversation**, with the same rights as the chat on the PC.
- Only the account of that conversation can press the buttons.
- No answer within a day counts as no.
- **Administrator rights** are never approved through Telegram. The bot says to do it on the PC, and you get a notification there.
- What asks every time (sending an e-mail, a button that buys something) has no **Always in this conversation**.

## Tasks and heartbeat
With **Tasks and heartbeat to Telegram too** on, this also goes to the account you paired first:
- a task that finished or stopped;
- what the heartbeat found;
- an approval a task needs (with buttons).

With Telegram on, WinAIMagic stays in the notification area at the bottom right when you close the window, so the bot keeps answering. When the PC sleeps, the messages come once it is awake again; Telegram keeps them up to a day.

## Privacy
Your messages and the answers go through Telegram's servers. They are not end-to-end encrypted there, because a bot cannot use a secret chat. So send nothing through Telegram you would not trust Telegram with. The model, your files and your settings stay on the PC. The PC calls Telegram itself; nothing is open to the internet.

## Problems
- **"Telegram does not know this token"**: the token is wrong or revoked. Remove it, make a new one with BotFather (**/token**) and paste that.
- **"Another program already uses this bot"**: a bot can be read by one program at a time. Stop the other program (MacAIMagic with the same bot, for instance), or make a separate bot for WinAIMagic.
- **No answer**: check that Telegram is on, that the status says "Connected" and that your account is under Who may talk. The log at the bottom shows what happened.
