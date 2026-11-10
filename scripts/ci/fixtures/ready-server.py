#!/usr/bin/env python3
"""An owned HTTP server for launcher controls, without a product build."""
from http.server import BaseHTTPRequestHandler, HTTPServer
import os
from pathlib import Path
from urllib.parse import urlsplit


class Handler(BaseHTTPRequestHandler):
    def do_GET(self):
        self.send_response(200)
        self.end_headers()
        self.wfile.write(b"Ready")

    def log_message(self, *_):
        pass


if os.getenv("HONUA_FIXTURE_STARTED_FILE"):
    Path(os.environ["HONUA_FIXTURE_STARTED_FILE"]).touch()
url = urlsplit(os.environ["ASPNETCORE_URLS"])
HTTPServer(("127.0.0.1", url.port), Handler).serve_forever()
