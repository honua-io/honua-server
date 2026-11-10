"""Disposable TLS identity provider for the published lifecycle journey."""
import base64
import hashlib
import json
import secrets
import ssl
import subprocess
import threading
import time
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from urllib.parse import parse_qs, urlencode, urlsplit


def base64url(value):
    return base64.urlsafe_b64encode(value).rstrip(b"=").decode()


def openssl(*args, content=None):
    result = subprocess.run(["openssl", *args], input=content, stdout=subprocess.PIPE,
                            stderr=subprocess.PIPE, timeout=30)
    if result.returncode:
        raise AssertionError("lifecycle identity cryptography failed")
    return result.stdout


def prepare_identity_files(directory):
    openssl("req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
            "-subj", "/CN=lifecycle-identity", "-addext",
            "subjectAltName=DNS:lifecycle-identity,DNS:localhost,IP:127.0.0.1",
            "-keyout", str(directory / "key.pem"), "-out", str(directory / "cert.pem"))
    openssl("pkcs12", "-export", "-out", str(directory / "server.pfx"),
            "-inkey", str(directory / "key.pem"), "-in", str(directory / "cert.pem"), "-passout", "pass:")
    for path in directory.iterdir():
        path.chmod(0o644)


class IdentityProvider:
    def __init__(self, directory, issuer):
        self.directory = directory
        self.issuer = issuer
        self.codes = {}
        self.lock = threading.Lock()
        modulus = openssl("rsa", "-in", str(directory / "key.pem"), "-noout", "-modulus")
        self.jwk = {"kty": "RSA", "alg": "RS256", "use": "sig", "kid": "lifecycle",
                    "n": base64url(bytes.fromhex(modulus.decode().strip().split("=", 1)[1])),
                    "e": "AQAB"}

    def authorize(self, query):
        callback = urlsplit(query.get("redirect_uri", ""))
        if (query.get("response_type") != "code" or query.get("client_id") != "lifecycle-console"
                or query.get("code_challenge_method") != "S256" or not query.get("code_challenge")
                or not query.get("state") or callback.scheme != "https"
                or callback.hostname != "localhost" or callback.path != "/admin/auth/callback"):
            raise ValueError("invalid authorization")
        code = secrets.token_urlsafe(32)
        with self.lock:
            self.codes[code] = (dict(query), time.monotonic() + 60)
        return code

    def exchange(self, form):
        with self.lock:
            pending = self.codes.pop(form.get("code"), None)
        if pending is None:
            raise ValueError("invalid authorization code")
        query, expires = pending
        challenge = base64url(hashlib.sha256(form.get("code_verifier", "").encode()).digest())
        if (time.monotonic() >= expires or form.get("grant_type") != "authorization_code"
                or form.get("client_id") != query["client_id"]
                or form.get("redirect_uri") != query["redirect_uri"]
                or not secrets.compare_digest(challenge, query["code_challenge"])):
            raise ValueError("invalid code exchange")
        now = int(time.time())
        claims = {"iss": self.issuer, "aud": query["client_id"], "sub": "lifecycle-person",
                  "name": "Lifecycle Person", "roles": ["admin"], "iat": now, "exp": now + 7200}
        header = base64url(json.dumps({"alg": "RS256", "kid": "lifecycle", "typ": "JWT"}).encode())
        payload = base64url(json.dumps(claims).encode())
        message = f"{header}.{payload}".encode()
        signature = openssl("dgst", "-sha256", "-sign", str(self.directory / "key.pem"), content=message)
        return {"token_type": "Bearer", "expires_in": 7200, "access_token": secrets.token_urlsafe(32),
                "id_token": message.decode() + "." + base64url(signature)}

    def start(self):
        self.server = ThreadingHTTPServer(("0.0.0.0", urlsplit(self.issuer).port), IdentityProviderHandler)
        self.server.provider = self
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
        context.load_cert_chain(self.directory / "cert.pem", self.directory / "key.pem")
        self.server.socket = context.wrap_socket(self.server.socket, server_side=True)
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(timeout=5)

class IdentityProviderHandler(BaseHTTPRequestHandler):
    def log_message(self, *_):
        pass

    def reply(self, value, status=200):
        content = json.dumps(value).encode()
        self.send_response(status)
        self.send_header("Content-Type", "application/json")
        self.send_header("Cache-Control", "no-store")
        self.send_header("Content-Length", str(len(content)))
        self.end_headers()
        self.wfile.write(content)

    def do_GET(self):
        path = urlsplit(self.path)
        if path.path == "/.well-known/openid-configuration":
            self.reply({"issuer": self.server.provider.issuer, "authorization_endpoint": self.server.provider.issuer + "/authorize",
                        "token_endpoint": self.server.provider.issuer + "/token", "jwks_uri": self.server.provider.issuer + "/jwks",
                        "response_types_supported": ["code"], "subject_types_supported": ["public"],
                        "id_token_signing_alg_values_supported": ["RS256"]})
        elif path.path == "/jwks":
            self.reply({"keys": [self.server.provider.jwk]})
        elif path.path == "/authorize":
            query = {key: values[0] for key, values in parse_qs(path.query).items()}
            try:
                code = self.server.provider.authorize(query)
            except ValueError:
                self.reply({"error": "invalid_request"}, 400)
                return
            self.send_response(302)
            self.send_header("Location", query["redirect_uri"] + "?" + urlencode({"code": code, "state": query["state"]}))
            self.end_headers()
        else:
            self.reply({}, 404)

    def do_POST(self):
        if self.path != "/token":
            self.reply({}, 404)
            return
        data = self.rfile.read(min(int(self.headers.get("Content-Length", "0")), 16384)).decode()
        try:
            self.reply(self.server.provider.exchange({key: values[0] for key, values in parse_qs(data).items()}))
        except ValueError:
            self.reply({"error": "invalid_grant"}, 400)
