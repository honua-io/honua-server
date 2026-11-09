#!/usr/bin/env python3
"""Bind subprocess readiness to its live Linux listener owner."""
import argparse
import importlib
import json
import os
from pathlib import Path
import subprocess
import re
import secrets
import sys
import time
from urllib.error import HTTPError, URLError
from urllib.parse import urljoin, urlsplit
from urllib.request import HTTPRedirectHandler, ProxyHandler, Request, build_opener

sys.path.insert(0, str(Path(__file__).resolve().parents[2] / "tests/python"))
process_ports = importlib.import_module("shared.process_ports")


def process_identity(pid):
    try:
        fields = Path(f"/proc/{pid}/stat").read_text().rpartition(")")[2].split()
    except FileNotFoundError:
        raise RuntimeError("launched server process exited") from None
    if fields[0] in {"Z", "X"}:
        raise RuntimeError("launched server process exited")
    return pid, fields[19]


def container_state(container):
    template = '{"id":"{{.Id}}","running":{{.State.Running}},"pid":{{.State.Pid}},"status":"{{.State.Status}}","started":"{{.State.StartedAt}}","ports":{{json .NetworkSettings.Ports}}}'
    result = subprocess.run(["docker", "inspect", "--format", template, container],
                            text=True, capture_output=True, timeout=5)
    if result.returncode:
        return None
    return json.loads(result.stdout)


def container_owns_listener(container, port):
    # Read descriptors as the container user; host users need not share its UID.
    script = '''ls /proc/1/fd >/dev/null 2>&1 || exit 2
for descriptor in /proc/[0-9]*/fd/*; do
    handle="$(readlink "$descriptor" 2>/dev/null)" || continue
    case "$handle" in socket:\\[*\\]) printf '%s\\n' "$handle" ;; esac
done'''
    result = subprocess.run(["docker", "exec", container, "/bin/sh", "-c", script],
                            text=True, capture_output=True, timeout=5)
    if result.returncode:
        raise RuntimeError("container listener ownership cannot be inspected")
    listeners = process_ports._listening_inodes(port)
    return bool(listeners) and listeners <= set(result.stdout.splitlines())


def compose_environment(path):
    text = Path(path).read_text()
    ports = dict(re.findall(r"\$\{([A-Z][A-Z0-9_]*PORT):-([0-9]+)\}", text))
    environment = {}
    allocated = set()
    for name, default in ports.items():
        selected = process_ports.server_port(name, int(default))
        while selected in allocated and not os.getenv(name) and os.getenv("CI", "").lower() not in {"1", "true", "yes"}:
            selected = process_ports.resolve_port(0)
        allocated.add(selected)
        environment[name] = str(selected)
    project = os.getenv("COMPOSE_PROJECT_NAME")
    if not project and os.getenv("CI", "").lower() not in {"1", "true", "yes"}:
        project = f"honua-{Path(path).parent.name}-{secrets.token_hex(6)}"
    if project:
        if not re.fullmatch(r"[a-z0-9][a-z0-9_-]*", project):
            raise ValueError("test Compose project name is invalid")
        environment["COMPOSE_PROJECT_NAME"] = project
    for name, value in environment.items():
        print(f"{name}={value}")


class NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, *_):
        return None


class ReadinessProbe:
    def __init__(self, options):
        self.options = options
        parsed = urlsplit(options.url)
        if parsed.scheme != "http" or parsed.hostname not in {"localhost", "127.0.0.1", "::1"}:
            raise ValueError("owned readiness requires a loopback HTTP URL")
        if parsed.username or parsed.password:
            raise ValueError("owned readiness URL cannot carry credentials")
        if not options.pid and not options.container:
            raise ValueError("owned readiness requires a launched PID or container ID")
        if options.container_port and (not options.container or parsed.hostname != "127.0.0.1"):
            raise ValueError("published container readiness requires a container and IPv4 loopback URL")
        self.port = parsed.port or 80
        self.host = parsed.hostname
        self.launcher = process_identity(options.pid) if options.pid else None
        self.owner = None
        self.client = build_opener(ProxyHandler({}), NoRedirect())

    def owns_listener(self):
        if self.launcher and process_identity(self.options.pid) != self.launcher:
            raise RuntimeError("launched server process identity changed")
        identity = None
        pid = self.options.pid
        if self.options.container:
            state = container_state(self.options.container)
            if state and state["running"]:
                pid = state["pid"]
                # Docker owns the container PID namespace; its PID need not exist in host procfs.
                identity = state["id"], state["started"], pid
            elif self.owner or (state and state["status"] in {"exited", "dead"}):
                raise RuntimeError("launched server container exited")
            else:
                return False
        identity = identity or process_identity(pid)
        if self.owner is not None and identity != self.owner:
            raise RuntimeError("server listener owner changed during readiness")
        self.owner = identity
        if self.options.container_port:
            bindings = (state.get("ports") or {}).get(f"{self.options.container_port}/tcp") or []
            return any(binding["HostPort"] == str(self.port) and
                       binding["HostIp"] in {self.host, "0.0.0.0"} for binding in bindings)
        if self.options.container:
            return container_owns_listener(state["id"], self.port)
        return process_ports.process_owns_listener(pid, self.port)

    def healthy(self):
        try:
            response = self.client.open(Request(self.options.url), timeout=2)
        except HTTPError as error:
            response = error
        except (OSError, URLError):
            return False
        try:
            with response:
                if response.status == 302 and getattr(self.options, "redirect_path", None):
                    source = urlsplit(self.options.url)
                    target = urlsplit(urljoin(self.options.url, response.headers.get("Location", "")))
                    return (target.scheme, target.netloc, target.path) == (
                        source.scheme, source.netloc, self.options.redirect_path) and self.options.body is None
                if response.status != 200:
                    return False
                return self.options.body is None or response.read(4096).decode() == self.options.body
        except (OSError, URLError):
            return False

    def wait(self):
        deadline = time.monotonic() + self.options.timeout
        while time.monotonic() < deadline:
            if self.owns_listener() and self.healthy() and self.owns_listener():
                return
            time.sleep(0.05)
        raise RuntimeError("server did not become healthy with owned listener identity")


def arguments():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    port = commands.add_parser("port")
    port.add_argument("--variable", required=True)
    port.add_argument("--ci-default", type=int, required=True)
    compose = commands.add_parser("compose-env")
    compose.add_argument("--file", required=True)
    for command in ("check", "wait"):
        probe = commands.add_parser(command)
        probe.add_argument("--pid", type=int)
        probe.add_argument("--container")
        probe.add_argument("--container-port", type=int)
        probe.add_argument("--url", required=True)
        probe.add_argument("--timeout", type=float, default=90)
        probe.add_argument("--body")
        probe.add_argument("--redirect-path", help="accept HTTP 302 to this same-origin path without following it")
    return parser.parse_args()


def main():
    options = arguments()
    try:
        if options.command == "port":
            print(process_ports.server_port(options.variable, options.ci_default))
            return 0
        if options.command == "compose-env":
            compose_environment(options.file)
            return 0
        probe = ReadinessProbe(options)
        if options.command == "check":
            return 0 if probe.owns_listener() else 1
        probe.wait()
        return 0
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError) as error:
        print(f"owned readiness refused: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
