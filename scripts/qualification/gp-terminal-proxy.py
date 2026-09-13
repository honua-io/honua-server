#!/usr/bin/env python3
"""Qualification-only RESP2 proxy: fence a successful terminal CAS acknowledgement.

The Redis write has committed when its :1 reply arrives. Withholding that reply
keeps the unmodified worker before queue cleanup and terminal callbacks. Readiness
is emitted only for explicitly armed jobs and successful CAS replies, never from
a timing guess or a terminal-status poll.
"""

import asyncio
import json
import os
from datetime import datetime, timezone
from pathlib import Path


async def frame(reader):
    line = await reader.readuntil(b"\r\n")
    kind, value = line[:1], line[1:-2]
    if kind == b"$":
        size = int(value)
        if size == -1:
            return line, None
        data = await reader.readexactly(size + 2)
        if data[-2:] != b"\r\n":
            raise ValueError("invalid RESP bulk terminator")
        return line + data, data[:-2]
    if kind == b"*":
        children = [await frame(reader) for _ in range(int(value))]
        return line + b"".join(c[0] for c in children), [c[1] for c in children]
    if kind in (b"+", b"-", b":"):
        return line, value
    raise ValueError("qualification proxy requires RESP2")


def terminal_job(command):
    if not isinstance(command, list) or not command or command[0].upper() not in (b"EVAL", b"EVALSHA"):
        return None
    for arg in command:
        if not isinstance(arg, bytes) or not arg.startswith(b"{"):
            continue
        try:
            record = json.loads(arg)
        except (ValueError, UnicodeDecodeError):
            continue
        if record.get("status") == "succeeded" and record.get("operationId"):
            return record
    return None


async def client(reader, writer):
    upstream_reader, upstream_writer = await asyncio.open_connection("redis", 6379)
    pending = asyncio.Queue()
    root = Path("/barriers")

    async def requests():
        while True:
            raw, command = await frame(reader)
            await pending.put(command)
            upstream_writer.write(raw)
            await upstream_writer.drain()

    async def replies():
        while True:
            command = await pending.get()
            raw, _ = await frame(upstream_reader)
            job = terminal_job(command)
            if job and raw == b":1\r\n":
                operation = job["operationId"].replace("/", "_").replace("\\", "_").replace("..", "_")
                directory = root / operation
                fence = directory / "terminal-committed-registration-pending"
                if fence.with_suffix(".arm").exists():
                    receipt = {"operationId": job["operationId"], "workerId": job.get("claimedBy"),
                               "barrier": fence.name, "observedAt": datetime.now(timezone.utc).isoformat(),
                               "redis_reply": ":1", "reply_forwarded": False, "terminal_record": job}
                    temporary = directory / f".terminal-{os.getpid()}.tmp"
                    temporary.write_text(json.dumps(receipt))
                    temporary.replace(fence.with_suffix(".ready.json"))
                    while not fence.with_suffix(".release").exists():
                        await asyncio.sleep(0.025)
            writer.write(raw)
            await writer.drain()

    tasks = [asyncio.create_task(requests()), asyncio.create_task(replies())]
    try:
        await asyncio.wait(tasks, return_when=asyncio.FIRST_COMPLETED)
    finally:
        for task in tasks:
            task.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        writer.close()
        upstream_writer.close()


async def main():
    server = await asyncio.start_server(client, "0.0.0.0", 6379)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    asyncio.run(main())
