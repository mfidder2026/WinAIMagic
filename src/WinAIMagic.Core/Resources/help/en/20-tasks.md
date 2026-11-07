---
title: Tasks and heartbeat
icon: Tasks
keywords: tasks task schedule scheduled background heartbeat checklist notification area tray start with windows notification notifications steps step list at once queue every day weekday
---
The model can also work while you are not beside it: run a **task** at a set moment, or now and then go through a **checklist**. Conversations can also work at the same time.

## Making a task
Choose **Tasks** in the sidebar and then **New task…**.
- **Name** and the **instruction for the model**: write it as a complete question, for example "See how much space is free on drive C: and name the largest folders in Downloads."
- **When**:

| Choice | Example |
|---|---|
| Once | tomorrow at 9:00 |
| Daily | every day, or only on weekdays, at 8:00 |
| Weekly | every Friday at 17:00 |
| Every few hours | every 2 hours from 8:00 to 22:00 |

- **Work folder** (optional): then the task can read and write files there.

You can also just ask in a conversation: *"Give me an overview of my drives every weekday at 8."* The model then plans the task, and the card first shows what and when.

> Tip: with **Run now** you try a task at once, without waiting for its time.

## What happens when a task runs
- Every run becomes a conversation of its own ("⏱ name · date"). You find it in the sidebar and under Tasks at **Recent runs**.
- When the model is not loaded, the task loads it. When the tasks are done and you did not use the model, it stops again (Settings → Tasks).
- When the task is done, Windows shows a **notification** with the first line of the answer. A click opens the conversation.
- Reading is fine. When the task wants to **change** something, you get a notification and the task waits until you approve or refuse in the app. After a day without an answer it counts as no.
- When the PC slept or the app was closed, a missed task runs once as soon as it can, if it is at most 12 hours late. An older one is skipped.

## Heartbeat
Settings → **Tasks** → **Heartbeat**. The model then goes through a checklist now and then, within the hours you choose, and only speaks up when there is something.
- **Open checklist** opens `HEARTBEAT.md`. Put your points below, one per line, for example "Is drive C: more than 90% full?"
- With nothing to report, no conversation stays behind, only "Last check 10:00: nothing to report".
- When the model is not loaded, the heartbeat skips, unless **The model may load for this** is on. With a large model that costs minutes and power every time.
- The heartbeat waits while you are chatting or a task runs.

## With the window closed
With tasks or a heartbeat on, WinAIMagic does not quit when you close the window. It stays at the bottom right in the **notification area** (sometimes behind the ^ arrow): right-click it to see whether the model is loaded, what runs and what the next task is. A click opens the window again, and **Quit WinAIMagic** really stops the app. The model stops when the window closes, so its memory is free again.
- **Start with Windows** (Settings → Tasks) starts WinAIMagic when you sign in, straight into the notification area, so tasks also run after a restart.
- A task can only run while the PC is on and awake. During a task the PC does not go to sleep (the screen may).

## Conversations at once
While the model works in one conversation, you can go on in another. The model writes one answer at a time. Your question then waits a moment ("Waiting for the model…") and is next after that. A conversation waiting for your approval does not hold up the others.
- In the sidebar a ring turns beside a conversation at work, and an orange dot means: waiting for your approval.
- You can stop each conversation on its own, also with a right-click in the sidebar.

## Steps
For a bigger job the model first lists the steps. You see them above the input field, and they are ticked off as it works.
