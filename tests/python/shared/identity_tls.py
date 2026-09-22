"""Owned integration-test TLS material; standard-library orchestration only."""
from __future__ import annotations

from contextlib import contextmanager
from dataclasses import dataclass
import os
from pathlib import Path
import re
import shutil
import socket
import ssl
import subprocess
import tempfile


class SecretRedactor:
    def __init__(self, environment: dict[str, str]):
        self.values = {value for key, value in environment.items()
                       if value and any(word in key.upper() for word in ('PASSWORD', 'TOKEN', 'SECRET', 'KEY'))}

    def add(self, value: str) -> None:
        if value:
            self.values.add(value)

    def __call__(self, text: str) -> str:
        for value in sorted(tuple(self.values), key=len, reverse=True):
            text = text.replace(value, '[REDACTED]')
        text = re.sub(r'(?i)([?&](?:token|access_token|api_key|password)=)[^\s&"<>]+', r'\1[REDACTED]', text)
        return re.sub(r'(?i)(authorization\s*[:=]\s*)[^\r\n]+', r'\1[REDACTED]', text)


def free_loopback_ports() -> tuple[int, int]:
    # Hold both while allocating so the two chosen endpoints cannot be identical.
    # Kestrel binds after release; an intervening collision must fail startup.
    with socket.socket() as first, socket.socket() as second:
        first.bind(('127.0.0.1', 0))
        second.bind(('127.0.0.1', 0))
        return first.getsockname()[1], second.getsockname()[1]


@dataclass(frozen=True)
class IdentityTls:
    ca: Path
    certificate: Path
    private_key: Path

    def context(self) -> ssl.SSLContext:
        return ssl.create_default_context(cafile=str(self.ca))


@contextmanager
def owned_identity_tls(parent: Path):
    """Generate per-session CA/leaf; no trust-store edits or committed key bytes."""
    executable = os.environ.get('HONUA_TEST_OPENSSL') or shutil.which('openssl')
    if not executable or not Path(executable).is_file():
        raise RuntimeError('Verified identity tests require OpenSSL; set HONUA_TEST_OPENSSL to its executable.')
    parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='identity-tls-', dir=parent) as name:
        directory = Path(name)
        directory.chmod(0o700)
        ca = directory / 'ca.crt'
        ca_key = directory / 'ca.key'
        key = directory / 'server.key'
        csr = directory / 'server.csr'
        certificate = directory / 'server.crt'
        extensions = directory / 'server.ext'
        extensions.write_text(
            'basicConstraints=critical,CA:FALSE\n'
            'keyUsage=critical,digitalSignature,keyEncipherment\n'
            'extendedKeyUsage=serverAuth\n'
            'subjectAltName=DNS:localhost,IP:127.0.0.1\n', encoding='ascii')
        commands = [
            ['req', '-x509', '-newkey', 'rsa:2048', '-nodes', '-sha256', '-days', '2',
             '-subj', '/CN=Honua owned integration CA', '-addext', 'basicConstraints=critical,CA:TRUE',
             '-addext', 'keyUsage=critical,keyCertSign,cRLSign', '-keyout', str(ca_key), '-out', str(ca)],
            ['req', '-newkey', 'rsa:2048', '-nodes', '-sha256', '-subj', '/CN=localhost',
             '-keyout', str(key), '-out', str(csr)],
            ['x509', '-req', '-in', str(csr), '-CA', str(ca), '-CAkey', str(ca_key),
             '-set_serial', '1', '-days', '2', '-sha256', '-extfile', str(extensions), '-out', str(certificate)],
        ]
        for arguments in commands:
            result = subprocess.run([executable, *arguments], capture_output=True, timeout=30,
                                    creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
            if result.returncode:
                raise RuntimeError('Owned identity certificate generation failed; no private material is logged.')
        for private_file in (ca_key, key):
            private_file.chmod(0o600)
        yield IdentityTls(ca, certificate, key)
