"""Exercise the destructive drill's Docker protocol without touching real stores."""
import importlib.util
import json
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


spec = importlib.util.spec_from_file_location("gp_store_recovery", Path(__file__).with_name("gp-store-recovery.py"))
recovery = importlib.util.module_from_spec(spec)
spec.loader.exec_module(recovery)


class FakeDocker:
    """Answers the drill's docker calls for one postgres volume and one bound output store."""

    def __init__(self, test, project, root, backup, failure):
        self.test, self.project, self.root, self.backup, self.failure = test, project, root, backup, failure
        self.calls, self.restored = [], set()

    def run(self, *args, **kwargs):
        self.calls.append(args)
        if args[:2] == ("docker", "compose"):
            return self.compose(args[6:])
        if args[:2] == ("docker", "inspect"):
            return self.inspect_container(args[2])
        if args[:3] == ("docker", "volume", "inspect"):
            owner = "another-project" if self.failure == "foreign-volume" else self.project
            return json.dumps([{"Labels": {"com.docker.compose.project": owner}}]).encode()
        self.test.assertEqual(args[:2], ("docker", "run"))
        return self.helper(args, kwargs)

    def compose(self, action):
        if action[:2] == ("ps", "-q"):
            return action[2].encode()
        if action == ("config", "--format", "json"):
            return json.dumps({"services": {"postgres": {"image": "postgres-helper"}}}).encode()
        if action == ("create", "postgres") and self.failure == "archive-corrupt":
            (self.backup / "postgres.tar").write_bytes(b"corrupted archive")
        return b""

    def inspect_container(self, service):
        mount = ({"Destination": "/var/lib/postgresql/data", "Type": "volume", "Name": "database"}
                 if service == "postgres" else
                 {"Destination": "/var/lib/honua/gp-outputs", "Type": "bind", "Source": str(self.root)})
        return json.dumps([{"Mounts": [mount]}]).encode()

    def helper(self, args, kwargs):
        mount = args[args.index("--mount") + 1]
        store = "postgres" if "src=database," in mount else "gp-output"
        if args[args.index("--entrypoint") + 1] == "tar":
            self.test.assertEqual(kwargs["input"], (store + " archive").encode())
            self.restored.add(store)
            return b""
        return self.helper_command(store, args[-1])

    def helper_command(self, store, command):
        if command.startswith("cd /volume"):
            if self.failure == "inventory-corrupt" and store in self.restored:
                return b"different restored inventory"
            return (store + " checksum file\n").encode()
        if command == "tar -C /volume -cpf - .":
            return (store + " archive").encode()
        if command == "find /volume -mindepth 1 -print -quit":
            return b"leftover" if self.failure == "nonempty-destination" else b""
        self.test.assertEqual(command, "find /volume -mindepth 1 -delete")
        return b""

    def inspect_destroyed(self, args, **_):
        self.calls.append(tuple(args))
        self.test.assertEqual(args, ["docker", "volume", "inspect", "database"])
        return subprocess.CompletedProcess(args, 0 if self.failure == "surviving-volume" else 1)


class RecoveryTests(unittest.TestCase):
    def exercise(self, failure=None):
        project = "honua-gp-reliability-test"
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp) / "output"
            root.mkdir()
            (root / ".honua-gp-store.json").write_text(json.dumps({"StoreReference": "qualification"}))
            backup = Path(temp) / "backup"
            docker = FakeDocker(self, project, root, backup, failure)
            with patch.object(recovery, "run", side_effect=docker.run), \
                    patch.object(recovery.subprocess, "run", side_effect=docker.inspect_destroyed):
                result = recovery.recover(Path("compose.yaml"), project, root, backup)
            self.assertEqual({p.name for p in backup.iterdir()}, {"postgres.tar", "gp-output.tar"})
        return result, docker.calls

    def test_restores_every_physical_store_after_destroying_originals(self):
        result, calls = self.exercise()
        self.assertEqual([s["store"] for s in result["substrates"]], ["postgres", "gp-output"])
        for store in result["substrates"]:
            self.assertTrue(store["original_destroyed"])
            self.assertTrue(store["restored_into_empty_store"])
            self.assertEqual(store["files_before"], store["files_after"])
            self.assertEqual(len(store["sha256"]), 64)
        actions = [c[6:] for c in calls if c[:2] == ("docker", "compose")]
        self.assertEqual(actions[-5:], [("stop", "server", "server-peer", "worker"),
                                       ("stop", "postgres"), ("down", "--volumes", "--remove-orphans"),
                                       ("create", "postgres"), ("up", "-d")])
        self.assertEqual({"server", "server-peer", "worker", "postgres"},
                         {action[2] for action in actions if action[:2] == ("ps", "-q")})
        archive = next(i for i, c in enumerate(calls) if c[-1] == "tar -C /volume -cpf - .")
        self.assertLess(next(i for i, c in enumerate(calls) if c[-2:] == ("stop", "postgres")), archive)
        destroyed = next(i for i, c in enumerate(calls) if c[-3:] == ("down", "--volumes", "--remove-orphans"))
        emptied = next(i for i, c in enumerate(calls) if c[-1] == "find /volume -mindepth 1 -delete")
        self.assertLess(destroyed, emptied)
        self.assertEqual(sum(c[-1] == "find /volume -mindepth 1 -print -quit" for c in calls), 2)

    def test_refuses_foreign_surviving_dirty_or_corrupt_stores(self):
        for failure, message in (("foreign-volume", "not owned"),
                                 ("surviving-volume", "survived destruction"),
                                 ("nonempty-destination", "not empty"),
                                 ("archive-corrupt", "checksum changed"),
                                 ("inventory-corrupt", "inventory differs")):
            with self.subTest(failure=failure), self.assertRaisesRegex((ValueError, RuntimeError), message):
                self.exercise(failure)


if __name__ == "__main__":
    unittest.main()
