"""A stand-in for Telegram's Bot API, for the tests of the Telegram bridge.

POST /bot<token>/<method> answers like Telegram (token TEST:OK only, else 401). The test drives it with
POST /control/push (an update; update_id is added) and reads GET /control/sent (every call the app made).
HTML with "<bad>" in it is refused with 400, as Telegram refuses HTML it cannot parse.
Usage: fake_telegram.py <port>
"""
import json, sys, threading, time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

TOKEN = "TEST:OK"
lock = threading.Condition()
updates = []
sent = []
next_update = [1]
next_message = [100]


class Handler(BaseHTTPRequestHandler):
    def log_message(self, *a):
        pass

    def reply(self, status, obj):
        body = json.dumps(obj).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        if self.path == "/control/sent":
            with lock:
                self.reply(200, sent)
        elif self.path.startswith("/file/bot" + TOKEN + "/"):
            body = b"\xff\xd8\xff\xe0fake-jpeg" if self.path.endswith(".jpg") else "regel een\nregel twee\n".encode()
            self.send_response(200)
            self.send_header("Content-Length", str(len(body)))
            self.end_headers()
            self.wfile.write(body)
        else:
            self.reply(404, {"ok": False})

    def do_POST(self):
        length = int(self.headers.get("Content-Length", 0))
        req = json.loads(self.rfile.read(length) or b"{}")
        if self.path == "/control/push":
            with lock:
                req["update_id"] = next_update[0]
                next_update[0] += 1
                updates.append(req)
                lock.notify_all()
            return self.reply(200, {"ok": True})
        parts = self.path.strip("/").split("/")
        if len(parts) != 2 or not parts[0].startswith("bot"):
            return self.reply(404, {"ok": False, "error_code": 404, "description": "Not Found"})
        if parts[0][3:] != TOKEN:
            return self.reply(401, {"ok": False, "error_code": 401, "description": "Unauthorized"})
        method = parts[1]
        if method == "getMe":
            return self.reply(200, {"ok": True, "result": {"id": 1, "is_bot": True, "first_name": "Test", "username": "testbot"}})
        if method == "getUpdates":
            offset = req.get("offset", 0)
            deadline = time.time() + req.get("timeout", 0)
            with lock:
                while True:
                    ready = [u for u in updates if u["update_id"] >= offset]
                    if ready or time.time() >= deadline:
                        break
                    lock.wait(max(0.05, deadline - time.time()))
            return self.reply(200, {"ok": True, "result": ready})
        with lock:
            sent.append({"method": method, **req})
        if method in ("sendMessage", "editMessageText"):
            if req.get("parse_mode") == "HTML" and "<bad>" in req.get("text", ""):
                return self.reply(400, {"ok": False, "error_code": 400, "description": "Bad Request: can't parse entities"})
            with lock:
                mid = req.get("message_id") or next_message[0]
                if method == "sendMessage":
                    next_message[0] += 1
            return self.reply(200, {"ok": True, "result": {"message_id": mid, "chat": {"id": req["chat_id"], "type": "private"}, "date": 0, "text": req.get("text")}})
        if method == "getFile":
            name = "photo.jpg" if "photo" in req.get("file_id", "") else "doc.txt"
            return self.reply(200, {"ok": True, "result": {"file_id": req["file_id"], "file_path": "files/" + name}})
        return self.reply(200, {"ok": True, "result": True})


if __name__ == "__main__":
    server = ThreadingHTTPServer(("127.0.0.1", int(sys.argv[1])), Handler)
    print("READY", flush=True)
    server.serve_forever()
