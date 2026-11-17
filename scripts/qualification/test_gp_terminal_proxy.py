"""Protocol-level and socket tests for the qualification commit fence."""

import asyncio
import importlib.util
import json
import os
from pathlib import Path
import struct
import sys
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("gp_terminal_proxy", Path(__file__).with_name("gp-terminal-proxy.py"))
PROXY = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = PROXY
SPEC.loader.exec_module(PROXY)
SQL = 'UPDATE "honua_jobs"."execution_jobs" SET status=$1, record=$2 WHERE operation_id=$3 AND version=$4'
JOB = {"operationId": "job-1", "status": 3, "claimedBy": "worker-1", "version": 4}


def message(kind, body=b""):
    return kind + struct.pack("!I", len(body) + 4) + body


def parse(sql=SQL, name=b"statement"):
    return name + b"\0" + sql.encode() + b"\0" + struct.pack("!H", 0)


def bind(job=JOB, portal=b"portal", statement=b"statement"):
    values = [b"Succeeded", json.dumps(job).encode(), b"job-1", struct.pack("!q", 4)]
    payload = portal + b"\0" + statement + b"\0" + struct.pack("!HH", 1, 1)
    payload += struct.pack("!H", len(values))
    payload += b"".join(struct.pack("!i", len(value)) + value for value in values)
    return payload + struct.pack("!H", 0)


def execute(protocol, sql=SQL, job=JOB):
    protocol.request(b"P", parse(sql))
    protocol.request(b"B", bind(job))
    protocol.request(b"E", b"portal\0" + struct.pack("!I", 0))
    protocol.request(b"S", b"")


def response(protocol, kind, body):
    return protocol.response(message(kind, body), kind, body)


class ProtocolTests(unittest.TestCase):
    def test_named_bind_waits_for_idle_commit_acknowledgement(self):
        protocol = PROXY.Protocol()
        execute(protocol)
        self.assertEqual((b"", None), response(protocol, b"C", b"UPDATE 1\0"))
        output, job = response(protocol, b"Z", b"I")
        self.assertEqual(JOB, job)
        self.assertEqual(message(b"C", b"UPDATE 1\0") + message(b"Z", b"I"), output)

    def test_nonterminal_select_and_cas_zero_do_not_fence(self):
        for sql, job, tag in [(SQL, JOB, b"UPDATE 0\0"),
                              ("SELECT $2", JOB, b"SELECT 1\0"),
                              (SQL, {**JOB, "status": 2}, b"UPDATE 1\0")]:
            with self.subTest(sql=sql, job=job, tag=tag):
                protocol = PROXY.Protocol()
                execute(protocol, sql, job)
                response(protocol, b"C", tag)
                self.assertIsNone(response(protocol, b"Z", b"I")[1])

    def test_record_must_match_updated_operation_and_status_parameter(self):
        for job in [{**JOB, "operationId": "other-job"}, {**JOB, "status": "failed"}]:
            protocol = PROXY.Protocol()
            execute(protocol, job=job)
            response(protocol, b"C", b"UPDATE 1\0")
            self.assertIsNone(response(protocol, b"Z", b"I")[1])

    def test_describe_and_prepared_statement_reuse_remain_eligible(self):
        protocol = PROXY.Protocol()
        protocol.request(b"P", parse())
        protocol.request(b"D", b"Sstatement\0")
        protocol.request(b"S", b"")
        response(protocol, b"1", b"")
        response(protocol, b"t", struct.pack("!H", 0))
        response(protocol, b"n", b"")
        response(protocol, b"Z", b"I")
        protocol.request(b"B", bind())
        protocol.request(b"D", b"Pportal\0")
        protocol.request(b"E", b"portal\0" + struct.pack("!I", 0))
        protocol.request(b"S", b"")
        response(protocol, b"C", b"UPDATE 1\0")
        self.assertEqual(JOB, response(protocol, b"Z", b"I")[1])

    def test_error_disables_fencing_even_when_connection_recovers(self):
        protocol = PROXY.Protocol()
        execute(protocol)
        response(protocol, b"E", b"SERROR\0\0")
        self.assertIsNone(response(protocol, b"Z", b"I")[1])
        execute(protocol)
        response(protocol, b"C", b"UPDATE 1\0")
        self.assertIsNone(response(protocol, b"Z", b"I")[1])

    def test_explicit_transaction_never_fences(self):
        for starting, ending in [(b"T", b"T"), (b"I", b"T"), (b"T", b"I")]:
            protocol = PROXY.Protocol()
            protocol.transaction = starting
            execute(protocol)
            response(protocol, b"C", b"UPDATE 1\0")
            self.assertIsNone(response(protocol, b"Z", ending)[1])

    def test_multiple_executions_in_one_sync_do_not_fence(self):
        protocol = PROXY.Protocol()
        protocol.request(b"P", parse())
        protocol.request(b"B", bind())
        for _ in range(2):
            protocol.request(b"E", b"portal\0" + struct.pack("!I", 0))
        protocol.request(b"S", b"")
        response(protocol, b"C", b"UPDATE 1\0")
        response(protocol, b"C", b"UPDATE 1\0")
        self.assertIsNone(response(protocol, b"Z", b"I")[1])

    def test_pipelined_sync_cycles_keep_their_own_records(self):
        protocol = PROXY.Protocol()
        execute(protocol)
        execute(protocol, job={**JOB, "claimedBy": "worker-2"})
        response(protocol, b"C", b"UPDATE 0\0")
        self.assertIsNone(response(protocol, b"Z", b"I")[1])
        response(protocol, b"C", b"UPDATE 1\0")
        self.assertEqual("worker-2", response(protocol, b"Z", b"I")[1]["claimedBy"])

    def test_unknown_portal_and_suspended_portal_do_not_fence(self):
        protocol = PROXY.Protocol()
        protocol.request(b"E", b"missing\0" + struct.pack("!I", 0))
        protocol.request(b"S", b"")
        response(protocol, b"C", b"UPDATE 1\0")
        self.assertIsNone(response(protocol, b"Z", b"I")[1])
        execute(protocol)
        response(protocol, b"s", b"")
        self.assertIsNone(response(protocol, b"Z", b"I")[1])


class SocketTests(unittest.IsolatedAsyncioTestCase):
    async def test_oversized_frame_is_rejected_before_payload_read(self):
        reader = asyncio.StreamReader()
        reader.feed_data(b"Q" + struct.pack("!I", PROXY.MAX_MESSAGE + 1))
        with self.assertRaises(ValueError):
            await PROXY.frame(reader)

    async def test_socket_fence_withholds_committed_reply_until_release(self):
        committed = asyncio.Event()
        sessions = []

        async def upstream(reader, writer):
            sessions.append(writer)
            await PROXY.startup_frame(reader)
            writer.write(message(b"R", struct.pack("!I", 0)) + message(b"Z", b"I"))
            await writer.drain()
            for _ in range(4):
                await PROXY.frame(reader)
            writer.write(message(b"1") + message(b"2") + message(b"C", b"UPDATE 1\0") + message(b"Z", b"I"))
            await writer.drain()
            committed.set()
            await reader.read()
            writer.close()

        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            fence = root / "job-1" / "terminal-committed-registration-pending"
            fence.parent.mkdir()
            fence.with_suffix(".arm").touch()
            database = await asyncio.start_server(upstream, "127.0.0.1", 0)
            port = database.sockets[0].getsockname()[1]
            settings = {"HONUA_GP_POSTGRES_HOST": "127.0.0.1", "HONUA_GP_POSTGRES_PORT": str(port)}
            with patch.dict(os.environ, settings):
                proxy = await asyncio.start_server(lambda r, w: PROXY.client(r, w, root), "127.0.0.1", 0)
                try:
                    await self.run_client(proxy, fence, committed)
                finally:
                    proxy.close()
                    await proxy.wait_closed()
                    for writer in sessions:
                        writer.close()
            database.close()
            await database.wait_closed()

    async def run_client(self, proxy, fence, committed):
        reader, writer = await asyncio.open_connection("127.0.0.1", proxy.sockets[0].getsockname()[1])
        try:
            startup = struct.pack("!I", 196608) + b"user\0test\0\0"
            writer.write(struct.pack("!I", len(startup) + 4) + startup)
            await writer.drain()
            await PROXY.frame(reader)
            await PROXY.frame(reader)
            writer.write(message(b"P", parse()) + message(b"B", bind())
                         + message(b"E", b"portal\0" + struct.pack("!I", 0)) + message(b"S"))
            await writer.drain()
            await asyncio.wait_for(committed.wait(), 2)
            await PROXY.frame(reader)  # ParseComplete and BindComplete may pass.
            await PROXY.frame(reader)
            for _ in range(100):
                if fence.with_suffix(".ready.json").exists():
                    break
                await asyncio.sleep(0.01)
            receipt = json.loads(fence.with_suffix(".ready.json").read_text())
            self.assertEqual("UPDATE 1", receipt["postgres_command_tag"])
            self.assertEqual("I", receipt["postgres_transaction_status"])
            self.assertFalse(receipt["reply_forwarded"])
            with self.assertRaises(asyncio.TimeoutError):
                await asyncio.wait_for(reader.readexactly(1), 0.05)
            fence.with_suffix(".release").touch()
            self.assertEqual(b"C", (await asyncio.wait_for(PROXY.frame(reader), 2))[1])
            self.assertEqual(b"I", (await PROXY.frame(reader))[2])
        finally:
            writer.close()
            await writer.wait_closed()


if __name__ == "__main__":
    unittest.main()
