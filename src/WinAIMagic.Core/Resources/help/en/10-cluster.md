---
title: Cluster: several PCs
icon: Network
keywords: cluster helper USB4 Thunderbolt ethernet cable several PCs memory RPC network
---
With several PCs there is one **main PC** (where you chat), and the others are **helpers** that keep part of the model on their graphics card. This works for models on the second engine.

## Setting up
1. Connect each helper to the main PC: a USB4 or Thunderbolt cable, or else ethernet.
2. On each helper: **Cluster** → role **Helper** → **Start helper**.
3. On the main PC the helpers are listed under **Cluster**, with their graphics card and memory. Tick them and start a model.

## Or ask the app
- On each other PC, tell WinAIMagic: "become a helper in the cluster". After a card the engine stops there and the helper starts.
- On the main PC say: "find all helpers, build the cluster and start the model". The model finds the helpers, ticks them after a card and starts the engine.

## Mac and Windows together
A Mac with MacAIMagic can be a helper for a PC, and the other way round: both speak the same cluster protocol. Between a Mac and a PC there is usually no direct cable: allow the local network on both. This has not been tested with real machines yet.

## Loading takes long, or seems stuck
- The first start sends each helper its part of the model. Over gigabit LAN that is about 100 MB/s, so 20 GB takes a few minutes; over Wi-Fi much longer. Under **Together** you see how much has been sent. The helper keeps what it received, so a next start is much faster.
- When a helper does not answer on its port, the engine starts without it and the app says why. Usually the firewall on that PC blocks the connection, or the network there is set to Public.
- When nothing goes to the helpers for a minute and a half, the app says so too.

## Safety
A helper only listens on the direct connection, unless you allow the local network. The connection has no password: use a direct cable or a network you trust.

> The first time, Windows may ask whether the app may pass the firewall. Only allow that for a private network.
