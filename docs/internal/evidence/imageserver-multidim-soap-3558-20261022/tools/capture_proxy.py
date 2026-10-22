#!/usr/bin/env python3
"""Loopback recording proxy for ArcGIS Pro ImageServer SOAP traffic (#3558).

Pro talks to http://127.0.0.1:<port>/<service path>; the proxy forwards to one
fixed upstream origin (the local Honua fixture or the public Esri reference)
and writes one JSON line per exchange.

What is persisted, and what is not:
- HTTP headers are never written (Authorization, cookies, Referer, agent).
- Query strings are dropped from the recorded path; only the service-relative
  path is kept.
- SOAP request bodies are written as a redacted *shape* (every scalar replaced
  by [REDACTED]) plus its SHA-256 fingerprint. A replayable copy of the request
  is written only when the body passes the credential scan below; elements whose
  names look like credentials are redacted in that copy as well.
- Response bodies are written as a redacted shape and fingerprint. Raw response
  bytes go to --raw-dir (outside the repository) for the A/B comparison step.
"""
from __future__ import annotations

import argparse
import hashlib
import http.client
import json
import re
import threading
import xml.etree.ElementTree as ET
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.parse import urlsplit

SOAP_NAMESPACES = {"http://schemas.xmlsoap.org/soap/envelope/", "http://www.w3.org/2003/05/soap-envelope"}
XSI = "http://www.w3.org/2001/XMLSchema-instance"
CREDENTIAL_NAME = re.compile(r"token|password|passwd|credential|secret|apikey|api_key|licen[cs]e|cookie|auth", re.I)
CREDENTIAL_TEXT = re.compile(r"token=|password=|bearer\s|apikey=|-----BEGIN", re.I)
URL_TEXT = re.compile(r"https?://[^/\s<\"']+", re.I)


def local(tag: str) -> str:
    return tag.rsplit("}", 1)[-1]


def parse_envelope(payload: bytes):
    if not payload or len(payload) > 64 * 1024 * 1024 or b"<!DOCTYPE" in payload.upper() or b"<!ENTITY" in payload.upper():
        return None
    try:
        root = ET.fromstring(payload)
    except ET.ParseError:
        return None
    namespace = root.tag.removeprefix("{").split("}")[0]
    if namespace not in SOAP_NAMESPACES or local(root.tag) != "Envelope":
        return None
    body = root.find(f"{{{namespace}}}Body")
    if body is None or len(body) != 1:
        return None
    return root, body[0]


def shape(element: ET.Element) -> ET.Element:
    """Copy element structure; scalars become [REDACTED] except xsi:type / xsi:nil."""
    clean = ET.Element(element.tag)
    for key in sorted(element.attrib):
        value = element.attrib[key]
        keep = key in {f"{{{XSI}}}type", f"{{{XSI}}}nil"}
        clean.set(key, value if keep else "[REDACTED]")
    if (element.text or "").strip():
        clean.text = "[REDACTED]"
    for child in element:
        clean.append(shape(child))
    return clean


def scrubbed(element: ET.Element) -> ET.Element:
    """Keep values for replay, but redact credential-named elements and URL origins."""
    clean = ET.Element(element.tag)
    for key, value in element.attrib.items():
        clean.set(key, "[REDACTED]" if CREDENTIAL_NAME.search(local(key)) else URL_TEXT.sub("{origin}", value))
    text = (element.text or "").strip()
    if text:
        clean.text = "[REDACTED]" if CREDENTIAL_NAME.search(local(element.tag)) else URL_TEXT.sub("{origin}", text)
    for child in element:
        clean.append(scrubbed(child))
    return clean


def digest(element: ET.Element) -> tuple[str, str]:
    xml = ET.tostring(element, encoding="unicode")
    return xml, hashlib.sha256(xml.encode()).hexdigest()


def describe(payload: bytes) -> dict | None:
    parsed = parse_envelope(payload)
    if parsed is None:
        return None
    root, operation = parsed
    shape_xml, shape_sha = digest(shape(operation))
    return {
        "operation": local(operation.tag),
        "namespace": operation.tag.removeprefix("{").split("}")[0] if operation.tag.startswith("{") else "",
        "shape_sha256": shape_sha,
        "shape_xml": shape_xml,
        "parameterless": not list(operation) and not (operation.text or "").strip(),
        "fault": local(operation.tag) == "Fault",
        "nil_result": any(e.get(f"{{{XSI}}}nil") in {"true", "1"} for e in operation.iter()),
    }


def binary_operation(payload: bytes) -> str | None:
    """Operation name of an Esri binary SOAP message (UTF-16 name after a 6-byte header)."""
    if len(payload) < 10 or payload[:6] != b"\x01\x00\x00\x00\x00\x00":
        return None
    count = int.from_bytes(payload[6:10], "little")
    if not 0 < count < 128 or len(payload) < 10 + count * 2:
        return None
    return payload[10:10 + count * 2].decode("utf-16-le", errors="replace").rstrip("\x00")


def replay_copy(payload: bytes) -> str | None:
    """Full scrubbed envelope for offline replay, or None when it fails the credential scan."""
    text = payload.decode("utf-8", errors="replace")
    if CREDENTIAL_TEXT.search(text):
        return None
    parsed = parse_envelope(payload)
    if parsed is None:
        return None
    root, _ = parsed
    xml = ET.tostring(scrubbed(root), encoding="unicode")
    return None if "[REDACTED]" in xml else xml


class Recorder:
    def __init__(self, upstream: str, log: Path, raw_dir: Path | None, port: int, force_xml_soap: bool = False):
        target = urlsplit(upstream)
        if target.scheme not in {"http", "https"} or target.username or target.password or target.query:
            raise ValueError("upstream must be a bare http(s) origin")
        self.target = target
        self.log = log
        self.raw_dir = raw_dir
        self.lock = threading.Lock()
        self.sequence = 0
        owner = self
        force_xml_soap = force_xml_soap

        class Handler(BaseHTTPRequestHandler):
            protocol_version = "HTTP/1.1"

            def log_message(self, *_args):
                pass

            def do_GET(self):
                self.forward()

            def do_POST(self):
                self.forward()

            def do_HEAD(self):
                self.forward()

            def forward(self):
                length = int(self.headers.get("Content-Length", "0") or 0)
                payload = self.rfile.read(length) if length > 0 else b""
                headers = {k: v for k, v in self.headers.items()
                           if k.lower() not in {"host", "connection", "accept-encoding", "content-length", "proxy-connection"}}
                prefix = target.path.rstrip("/")
                try:
                    cls = http.client.HTTPSConnection if target.scheme == "https" else http.client.HTTPConnection
                    connection = cls(target.hostname, target.port, timeout=300)
                    connection.request(self.command, prefix + self.path, body=payload or None, headers=headers)
                    response = connection.getresponse()
                    content = response.read()
                    status, response_headers = response.status, response.getheaders()
                    connection.close()
                    if force_xml_soap and b"esriServiceCatalogMessageFormatSoapOrBin" in content:
                        # Keep Pro on XML SOAP so the reference and Honua are compared in one encoding.
                        content = content.replace(b"esriServiceCatalogMessageFormatSoapOrBin",
                                                  b"esriServiceCatalogMessageFormatSoap")
                except (OSError, http.client.HTTPException):
                    status, response_headers, content = 502, [("Content-Type", "text/plain")], b"upstream transport failed"
                owner.record(self.command, self.path, payload, status, response_headers, content,
                             self.headers.get("Content-Type", ""))
                self.send_response(status)
                for key, value in response_headers:
                    if key.lower() not in {"connection", "transfer-encoding", "content-length", "keep-alive"}:
                        self.send_header(key, value)
                self.send_header("Content-Length", str(len(content)))
                self.end_headers()
                if self.command != "HEAD":
                    self.wfile.write(content)

        self.server = ThreadingHTTPServer(("127.0.0.1", port), Handler)

    def record(self, method, path, payload, status, headers, content, request_content_type=""):
        content_type = next((v for k, v in headers if k.lower() == "content-type"), "")
        with self.lock:
            self.sequence += 1
            entry = {
                "sequence": self.sequence,
                "method": method,
                "path": urlsplit(path).path,
                # Parameter names only; values (including any token) are never written.
                "query_keys": sorted({part.split("=", 1)[0].lower() for part in urlsplit(path).query.split("&") if part}),
                "request_content_type": request_content_type.split(";")[0].strip(),
                "binary_request_operation": binary_operation(payload),
                "binary_response_operation": binary_operation(content),
                "status": status,
                "content_type": content_type.split(";")[0].strip(),
                "response_bytes": len(content),
                "response_sha256": hashlib.sha256(content).hexdigest(),
                "request": describe(payload),
                "response": describe(content),
            }
            if entry["request"] is not None:
                entry["request"]["replay_xml"] = replay_copy(payload)
            if self.raw_dir is not None:
                self.raw_dir.mkdir(parents=True, exist_ok=True)
                (self.raw_dir / f"{self.sequence:05d}.response").write_bytes(content)
            with self.log.open("a", encoding="utf-8") as handle:
                handle.write(json.dumps(entry, sort_keys=True) + "\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--upstream", required=True, help="http(s) origin[/prefix] to forward to")
    parser.add_argument("--port", type=int, required=True)
    parser.add_argument("--log", type=Path, required=True)
    parser.add_argument("--raw-dir", type=Path)
    parser.add_argument("--force-xml-soap", action="store_true",
                        help="rewrite GetMessageFormats SoapOrBin to Soap so Pro stays on XML SOAP")
    args = parser.parse_args()
    args.log.parent.mkdir(parents=True, exist_ok=True)
    recorder = Recorder(args.upstream, args.log, args.raw_dir, args.port, args.force_xml_soap)
    print(f"recording 127.0.0.1:{args.port} -> {urlsplit(args.upstream).scheme}://{urlsplit(args.upstream).hostname}", flush=True)
    recorder.server.serve_forever()


if __name__ == "__main__":
    main()
