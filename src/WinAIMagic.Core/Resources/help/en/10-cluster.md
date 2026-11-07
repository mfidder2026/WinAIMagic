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

## Safety
A helper only listens on the direct connection, unless you allow the local network. The connection has no password: use a direct cable or a network you trust.

> The first time, Windows may ask whether the app may pass the firewall. Only allow that for a private network.
