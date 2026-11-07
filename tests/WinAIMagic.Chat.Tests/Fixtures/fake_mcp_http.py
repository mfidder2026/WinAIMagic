#!/usr/bin/env python3
"""An MCP server over Streamable HTTP for the tests, with an OAuth authorization server in the same process.
Usage: fake_mcp_http.py <port> [oauth]   prints READY when it listens."""
import json, sys, hashlib, base64, urllib.parse
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

PORT = int(sys.argv[1]); OAUTH = len(sys.argv) > 2 and sys.argv[2] == "oauth"
BASE = f"http://127.0.0.1:{PORT}"
codes = {}          # code -> challenge
TOKEN = "token-123"

def rpc(msg):
    m, mid = msg.get("method"), msg.get("id")
    if mid is None: return None
    if m == "initialize":
        return {"jsonrpc": "2.0", "id": mid, "result": {"protocolVersion": "2025-06-18", "capabilities": {"tools": {}}, "serverInfo": {"name": "http-fake"}}}
    if m == "tools/list":
        return {"jsonrpc": "2.0", "id": mid, "result": {"tools": [{"name": "hello", "description": "Say hello", "inputSchema": {"type": "object"}}]}}
    if m == "tools/call":
        return {"jsonrpc": "2.0", "id": mid, "result": {"content": [{"type": "text", "text": "hello over http"}]}}
    return {"jsonrpc": "2.0", "id": mid, "error": {"code": -32601, "message": "nope"}}

class H(BaseHTTPRequestHandler):
    def log_message(self, *a): pass
    def out(self, code, body=b"", ctype="application/json", headers=None):
        self.send_response(code)
        for k, v in (headers or {}).items(): self.send_header(k, v)
        if body: self.send_header("Content-Type", ctype)
        self.send_header("Content-Length", str(len(body))); self.end_headers(); self.wfile.write(body)
    def do_GET(self):
        u = urllib.parse.urlparse(self.path); q = dict(urllib.parse.parse_qsl(u.query))
        if u.path.startswith("/.well-known/oauth-protected-resource"):
            return self.out(200, json.dumps({"resource": BASE + "/mcp", "authorization_servers": [BASE], "scopes_supported": ["read"]}).encode())
        if u.path == "/.well-known/oauth-authorization-server":
            return self.out(200, json.dumps({"issuer": BASE, "authorization_endpoint": BASE + "/authorize", "token_endpoint": BASE + "/token",
                                             "registration_endpoint": BASE + "/register"}).encode())
        if u.path == "/authorize":
            # the user "logs in": straight back to the app with a code
            code = "code-" + q["state"][:6]; codes[code] = q["code_challenge"]
            loc = q["redirect_uri"] + "?" + urllib.parse.urlencode({"code": code, "state": q["state"]})
            return self.out(302, headers={"Location": loc})
        self.out(404)
    def do_DELETE(self): self.out(200)
    def do_POST(self):
        body = self.rfile.read(int(self.headers.get("Content-Length", 0)))
        if self.path == "/register":
            req = json.loads(body); assert req["redirect_uris"][0].startswith("http://127.0.0.1:")
            return self.out(201, json.dumps({"client_id": "client-1"}).encode())
        if self.path == "/token":
            f = dict(urllib.parse.parse_qsl(body.decode()))
            if f.get("grant_type") == "authorization_code":
                ch = base64.urlsafe_b64encode(hashlib.sha256(f["code_verifier"].encode()).digest()).rstrip(b"=").decode()
                if codes.get(f["code"]) != ch: return self.out(400, json.dumps({"error": "invalid_grant"}).encode())
            return self.out(200, json.dumps({"access_token": TOKEN, "refresh_token": "r", "expires_in": 3600}).encode())
        if self.path == "/mcp":
            if OAUTH and self.headers.get("Authorization") != "Bearer " + TOKEN:
                return self.out(401, headers={"WWW-Authenticate": f'Bearer resource_metadata="{BASE}/.well-known/oauth-protected-resource"'})
            msg = json.loads(body); reply = rpc(msg)
            if reply is None: return self.out(202)
            if msg.get("method") == "tools/list":   # answer this one as server-sent events
                ev = b"event: message\ndata: " + json.dumps(reply).encode() + b"\n\n"
                return self.out(200, ev, "text/event-stream", {"Mcp-Session-Id": "s1"})
            return self.out(200, json.dumps(reply).encode(), headers={"Mcp-Session-Id": "s1"})
        self.out(404)

srv = ThreadingHTTPServer(("127.0.0.1", PORT), H)
print("READY", flush=True)
srv.serve_forever()
