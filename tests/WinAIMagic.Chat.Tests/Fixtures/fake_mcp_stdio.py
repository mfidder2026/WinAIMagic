#!/usr/bin/env python3
"""A tiny MCP server on stdio for the tests: two pages of tools, an echo, an error, and a ping from the server."""
import json, sys

print("hello from the login shell")          # not JSON-RPC: the client must only log it
sys.stdout.flush()
print("starting", file=sys.stderr); sys.stderr.flush()

TOOLS = [
    {"name": "echo", "description": "Echo the text", "inputSchema": {"type": "object", "properties": {"text": {"type": "string"}}},
     "annotations": {"readOnlyHint": True}},
    {"name": "delete_all", "description": "Deletes everything", "inputSchema": {"type": "object"}, "annotations": {"destructiveHint": True}},
    {"name": "fail", "description": "Always fails", "inputSchema": {"type": "object"}},
]

def send(msg):
    sys.stdout.write(json.dumps(msg) + "\n"); sys.stdout.flush()

for line in sys.stdin:
    msg = json.loads(line)
    method, mid = msg.get("method"), msg.get("id")
    if mid is None:
        continue
    if method == "initialize":
        send({"jsonrpc": "2.0", "id": mid, "result": {"protocolVersion": "2025-06-18", "capabilities": {"tools": {"listChanged": True}},
              "serverInfo": {"name": "fake", "version": "1"}, "instructions": "be nice"}})
        send({"jsonrpc": "2.0", "id": 900, "method": "ping"})      # a request from the server
    elif method == "tools/list":
        cursor = (msg.get("params") or {}).get("cursor")
        if cursor is None:
            send({"jsonrpc": "2.0", "id": mid, "result": {"tools": TOOLS[:2], "nextCursor": "page2"}})
        else:
            send({"jsonrpc": "2.0", "id": mid, "result": {"tools": TOOLS[2:]}})
    elif method == "tools/call":
        p = msg["params"]
        if p["name"] == "echo":
            send({"jsonrpc": "2.0", "id": mid, "result": {"content": [{"type": "text", "text": "echo: " + p["arguments"].get("text", "")},
                                                                    {"type": "image", "data": "AA==", "mimeType": "image/png"}]}})
        elif p["name"] == "fail":
            send({"jsonrpc": "2.0", "id": mid, "result": {"content": [{"type": "text", "text": "it broke"}], "isError": True}})
        else:
            send({"jsonrpc": "2.0", "id": mid, "error": {"code": -32602, "message": "unknown tool"}})
    else:
        send({"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": "method not found"}})
