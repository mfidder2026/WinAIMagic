---
title: Connectors
icon: Connect
keywords: connectors MCP tools GitHub files fetch web import Claude Desktop Claude Code log in OAuth token npx uvx credential manager
---
A **connector** gives the model tools of another program or a service, through the Model Context Protocol (MCP). For example GitHub, or files outside the work folder.

## Adding a connector
Open Settings → **Connectors**.
- **Add example** offers connectors that work right away:

| Example | What it does | Needs |
|---|---|---|
| Files | read, search and write in Documents, Downloads and Desktop | `npx` (winget install OpenJS.NodeJS.LTS) |
| Fetch web | fetch a web page as text | `uvx` (winget install astral-sh.uv) |
| GitHub | repositories, issues and pull requests | a personal token from GitHub |

- **Add…** for a connector of your own: a *program on this PC* (command and arguments, as in Claude Desktop) or an *internet address*. A program that is not an .exe, such as `npx`, starts through the command prompt.
- **Import** takes connectors over from Claude Desktop and Claude Code. They come in switched off, so you can look first.
- **Test connection** starts the connector briefly and shows which tools it has.

The connectors are in `mcp.json` in the app's data folder, in the same format as Claude Desktop's.

## How the model uses them
The model only sees the names of the connectors that are on. Only when it needs one does it load its tools ("Load connector: GitHub"). They then stay in that conversation, also after a restart of the app. That keeps the prompt short and the model fast. A connector with **Always loaded** is there in every conversation.

## Rights
Per connector you choose **Allow**, **Ask first** or **Deny**.
- A tool that says it only reads is allowed by default (except with Deny).
- Other tools ask first. The card shows the connector, the tool and exactly what is passed along.
- A red warning is added when a tool can delete something, when the connector is on the internet, or when the call follows reading the web. After reading the web even Allow asks first.

## Logging in to a service
When a connector on the internet asks you to log in, the app opens the login page in your own browser. After that it goes on by itself. The token goes into the **Windows Credential Manager**; WinAIMagic never sees your password. **Log out** is in the connector's menu. For a service without a login page (such as GitHub) you fill in a token; that goes into the Credential Manager too.

## Calendar and Reminders
MacAIMagic has a built-in connector for the macOS calendar and reminders. WinAIMagic does not. You can add a connector for Outlook yourself if you have one.

> A connector on the internet sends what the model passes along to that service. With Ask first, look at the card carefully.

## Something does not work
Open **Log** in the connector's menu: it shows what the program or service reported. When `npx` or `uvx` is missing, the app says how to install it.
