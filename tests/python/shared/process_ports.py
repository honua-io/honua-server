"""Port selection and Linux listener ownership for subprocess test servers."""
import os
from pathlib import Path
import socket


def resolve_port(port: int) -> int:
    if port == 0:
        with socket.socket() as listener:
            listener.bind(("127.0.0.1", 0))
            return listener.getsockname()[1]
    if not 1 <= port <= 65535:
        raise ValueError("test server port must be between 1 and 65535, or zero for allocation")
    return port


def server_port(variable: str, ci_default: int, worker_index: int = 0) -> int:
    default = ci_default if os.getenv("CI", "").lower() in {"1", "true", "yes"} else 0
    try:
        selected = int(os.getenv(variable, str(default)))
    except ValueError as error:
        raise ValueError(f"invalid test server port in {variable}") from error
    if selected == 0:
        return resolve_port(0)
    resolve_port(selected)
    return resolve_port(selected + worker_index)


def _children(pid: int) -> set[int]:
    children = set()
    try:
        tasks = list(Path(f"/proc/{pid}/task").iterdir())
    except FileNotFoundError:
        return children
    for task in tasks:
        try:
            children.update(map(int, (task / "children").read_text().split()))
        except FileNotFoundError:
            continue
    return children


def _process_tree(pid: int) -> set[int]:
    visited = set()
    pending = [pid]
    while pending:
        current = pending.pop()
        if current not in visited:
            visited.add(current)
            pending.extend(_children(current) - visited)
    return visited


def _table_listeners(table: Path, port: int) -> set[str]:
    if not table.exists():
        return set()
    inodes = set()
    for line in table.read_text().splitlines()[1:]:
        fields = line.split()
        if fields[3] == "0A" and int(fields[1].rpartition(":")[2], 16) == port:
            inodes.add(f"socket:[{fields[9]}]")
    return inodes


def _listening_inodes(port: int) -> set[str]:
    return _table_listeners(Path("/proc/net/tcp"), port) | _table_listeners(Path("/proc/net/tcp6"), port)


def _socket_handles(pid: int) -> set[str]:
    handles = set()
    try:
        descriptors = list(Path(f"/proc/{pid}/fd").iterdir())
    except FileNotFoundError:
        return handles
    for descriptor in descriptors:
        try:
            handles.add(os.readlink(descriptor))
        except FileNotFoundError:
            continue
    return handles


def process_owns_listener(pid: int, port: int) -> bool:
    if not Path("/proc/net/tcp").is_file():
        raise RuntimeError("test server ownership requires Linux procfs; readiness cannot be verified")
    try:
        listeners = _listening_inodes(port)
        owned = set().union(*(_socket_handles(child) for child in _process_tree(pid)))
    except PermissionError as error:
        raise RuntimeError("test server listener ownership cannot be inspected") from error
    return bool(listeners) and listeners <= owned
