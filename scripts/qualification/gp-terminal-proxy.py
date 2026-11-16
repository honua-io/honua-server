#!/usr/bin/env python3
"""Qualification-only PostgreSQL proxy withholding a committed terminal CAS reply.

A successful CommandComplete alone does not prove commit. Retain it until the
matching ReadyForQuery reports idle, then publish the armed barrier before either
message reaches Npgsql. Unsupported/error flows never publish a receipt.
"""

import asyncio
from collections import deque
from dataclasses import dataclass, field
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import re
import struct

MAX_MESSAGE = 8 * 1024 * 1024
MAX_BUFFER = 16 * 1024 * 1024
MAX_TRACKED = 512


async def frame(reader, startup=False):
    prefix = b"" if startup else await reader.readexactly(1)
    length = await reader.readexactly(4)
    size = struct.unpack("!I", length)[0]
    if not 4 <= size <= MAX_MESSAGE:
        raise ValueError("invalid or oversized PostgreSQL message")
    body = await reader.readexactly(size - 4)
    return prefix + length + body, prefix, body


class Fields:
    def __init__(self, body):
        self.body, self.offset = body, 0

    def take(self, size):
        if size < 0 or self.offset + size > len(self.body):
            raise ValueError("truncated PostgreSQL message")
        start, self.offset = self.offset, self.offset + size
        return self.body[start:self.offset]

    def number(self, code):
        return struct.unpack("!" + code, self.take(struct.calcsize(code)))[0]

    def string(self):
        end = self.body.find(b"\0", self.offset)
        if end < 0:
            raise ValueError("unterminated PostgreSQL string")
        return self.take(end - self.offset + 1)[:-1].decode("utf-8")

    def end(self):
        if self.offset != len(self.body):
            raise ValueError("unexpected PostgreSQL message data")


def parse_statement(body):
    fields = Fields(body)
    name, sql = fields.string(), fields.string()
    fields.take(fields.number("H") * 4)
    fields.end()
    return name, (sql, ())


def bind_portal(body, statements):
    fields = Fields(body)
    name, statement = fields.string(), fields.string()
    formats = [fields.number("H") for _ in range(fields.number("H"))]
    values = []
    for _ in range(fields.number("H")):
        length = fields.number("i")
        values.append(None if length == -1 else fields.take(length))
    for _ in range(fields.number("H")):
        fields.number("H")
    fields.end()
    if len(formats) not in (0, 1, len(values)) or any(f not in (0, 1) for f in formats):
        raise ValueError("unsupported parameter format")
    return name, terminal_job(statements.get(statement), values)


def terminal_job(statement, values):
    if statement is None:
        return None
    sql, _ = statement
    # Only the store's versioned CAS is eligible, including its optional lease CTE.
    update = re.search(r'\bUPDATE\s+(?:"?\w+"?\.)?"?execution_jobs"?\s+SET\b', sql, re.I)
    if not update or ";" in sql or "--" in sql or "/*" in sql:
        return None
    sql = sql[update.end():]
    record = re.search(r'\brecord\s*=\s*\$(\d+)\b', sql, re.I)
    status = re.search(r'\bstatus\s*=\s*\$(\d+)\b', sql, re.I)
    cas = re.search(r'\bWHERE\s+operation_id\s*=\s*\$(\d+)\s+AND\s+version\s*=\s*\$\d+\b', sql, re.I)
    if not record or not status or not cas:
        return None
    try:
        raw = values[int(record[1]) - 1]
        state = values[int(status[1]) - 1]
        if state != b"Succeeded" or raw is None:
            return None
        job = json.loads(raw)
        if not isinstance(job, dict) or values[int(cas[1]) - 1] != job.get("operationId", "").encode():
            return None
    except (IndexError, ValueError, TypeError, UnicodeDecodeError):
        return None
    if isinstance(job, dict) and job.get("status") in (3, "succeeded") and isinstance(job.get("operationId"), str) and job["operationId"]:
        return job
    return None


@dataclass
class Cycle:
    jobs: list = field(default_factory=list)
    commands: int = 0
    candidate: dict | None = None
    failed: bool = False
    closed: bool = False
    buffered: bytearray = field(default_factory=bytearray)


class Protocol:
    def __init__(self):
        self.statements, self.portals = {}, {}
        self.cycles = deque()
        self.current = None
        self.transaction = b"I"
        self.enabled = True

    def cycle(self):
        if self.current is None:
            self.current = Cycle()
            self.cycles.append(self.current)
        if len(self.cycles) > MAX_TRACKED:
            raise ValueError("too many pipelined cycles")
        return self.current

    def request(self, kind, body):
        if kind == b"P":
            name, statement = parse_statement(body)
            self.statements[name] = statement
            self.cycle()
        elif kind == b"B":
            name, job = bind_portal(body, self.statements)
            self.portals[name] = job
            self.cycle()
        elif kind == b"E":
            fields = Fields(body)
            name, rows = fields.string(), fields.number("I")
            fields.end()
            self.cycle().jobs.append(self.portals.get(name) if rows == 0 else None)
        elif kind in (b"S", b"Q"):
            cycle = self.cycle()
            cycle.closed = True
            if kind == b"Q":
                cycle.failed = True  # Simple-query SQL is intentionally not inferred.
            self.current = None
        elif kind == b"C":
            fields = Fields(body)
            target, name = fields.take(1), fields.string()
            fields.end()
            (self.statements if target == b"S" else self.portals).pop(name, None)
            self.cycle()
        elif kind == b"D":
            fields = Fields(body)
            if fields.take(1) not in (b"S", b"P"):
                raise ValueError("unsupported Describe target")
            fields.string()
            fields.end()
            self.cycle()
        elif kind not in (b"H", b"p", b"X"):
            self.enabled = False
        self.limit_tracking()

    def limit_tracking(self):
        jobs = list(self.portals.values()) + [job for cycle in self.cycles for job in cycle.jobs]
        if max(len(self.statements), len(jobs)) > MAX_TRACKED:
            raise ValueError("too many tracked PostgreSQL objects")
        retained = sum(len(statement[0].encode()) for statement in self.statements.values())
        retained += sum(len(json.dumps(job)) for job in jobs if job is not None)
        if retained > MAX_BUFFER:
            raise ValueError("PostgreSQL tracking buffer exceeded")

    def response(self, raw, kind, body):
        cycle = self.cycles[0] if self.cycles else None
        if kind == b"E":
            self.enabled = False  # Names may have been replaced by an ignored Parse/Bind.
        if cycle is None:
            if kind == b"Z":
                self.transaction = body
            return raw, None
        if kind in (b"E", b"s", b"G", b"H", b"W", b"I"):
            cycle.failed = True
        if kind == b"C":
            self.complete(cycle, body)
        if cycle.candidate is not None:
            cycle.buffered.extend(raw)
            if len(cycle.buffered) > MAX_BUFFER:
                raise ValueError("terminal acknowledgement buffer exceeded")
        if kind != b"Z":
            return (b"" if cycle.buffered else raw), None
        return self.ready(cycle, raw, body)

    def complete(self, cycle, body):
        index = cycle.commands
        cycle.commands += 1
        if index < len(cycle.jobs) and body == b"UPDATE 1\0":
            job = cycle.jobs[index]
            if self.enabled and self.transaction == b"I" and job is not None:
                cycle.candidate = job

    def ready(self, cycle, raw, body):
        if not cycle.closed or body not in (b"I", b"T", b"E"):
            raise ValueError("unexpected ReadyForQuery")
        job = cycle.candidate if (self.enabled and not cycle.failed and body == b"I"
                                    and len(cycle.jobs) == cycle.commands == 1) else None
        self.cycles.popleft()
        self.transaction = body
        self.portals.clear()  # Unnamed and named portals expire at transaction end.
        return bytes(cycle.buffered) if cycle.buffered else raw, job


async def barrier(root, job):
    operation = job["operationId"].replace("/", "_").replace("\\", "_").replace("..", "_")
    fence = root / operation / "terminal-committed-registration-pending"
    if not fence.with_suffix(".arm").exists():
        return
    receipt = {"operationId": job["operationId"], "workerId": job.get("claimedBy"),
               "barrier": fence.name, "observedAt": datetime.now(timezone.utc).isoformat(),
               "postgres_command_tag": "UPDATE 1", "postgres_transaction_status": "I",
               "reply_forwarded": False, "terminal_record": job}
    temporary = fence.parent / f".terminal-{os.getpid()}.tmp"
    temporary.write_text(json.dumps(receipt))
    temporary.replace(fence.with_suffix(".ready.json"))
    while not fence.with_suffix(".release").exists():
        await asyncio.sleep(0.025)


async def startup(reader, writer, upstream):
    raw, _, body = await frame(reader, startup=True)
    if body[:4] in (struct.pack("!I", 80877103), struct.pack("!I", 80877104)):
        writer.write(b"N")  # The qualification network uses SSL Mode=Disable.
        await writer.drain()
        raw, _, body = await frame(reader, startup=True)
    if len(body) < 4 or struct.unpack("!I", body[:4])[0] not in (196608, 80877102):
        raise ValueError("unsupported PostgreSQL startup")
    upstream.write(raw)
    await upstream.drain()


async def pump_requests(reader, upstream, protocol):
    while True:
        raw, kind, body = await frame(reader)
        protocol.request(kind, body)
        upstream.write(raw)
        await upstream.drain()


async def pump_replies(reader, writer, protocol, root):
    while True:
        raw, kind, body = await frame(reader)
        output, job = protocol.response(raw, kind, body)
        if job is not None:
            await barrier(root, job)
        writer.write(output)
        await writer.drain()


async def client(reader, writer, root=Path("/barriers")):
    upstream_reader, upstream = await asyncio.open_connection(
        os.environ.get("HONUA_GP_POSTGRES_HOST", "postgres"),
        int(os.environ.get("HONUA_GP_POSTGRES_PORT", "5432")))
    tasks = []
    try:
        await startup(reader, writer, upstream)
        protocol = Protocol()
        tasks = [asyncio.create_task(pump_requests(reader, upstream, protocol)),
                 asyncio.create_task(pump_replies(upstream_reader, writer, protocol, root))]
        done, _ = await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
        for task in done:
            task.result()
    except (asyncio.IncompleteReadError, ConnectionError):
        pass
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        writer.close()
        upstream.close()
        await asyncio.gather(writer.wait_closed(), upstream.wait_closed(), return_exceptions=True)


async def main():
    server = await asyncio.start_server(client, "0.0.0.0", 5432)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
