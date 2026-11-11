#!/usr/bin/env python3
"""Exercise publication side effects and live gate/race admission."""
import importlib.util
import json
from pathlib import Path
import subprocess
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("publish", Path(__file__).resolve().parents[1] / "push-ready-pr.py")
publish = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publish)
REPO = "honua-io/honua-server"


def pr(head="old"):
    return {"number": 1, "head": {"ref": "topic", "sha": head,
                                  "repo": {"full_name": REPO}}}


class PublishTests(unittest.TestCase):
    def run_publish(self, pulls=None, runs=None, mutation=None, fail=None, branch="topic", remote=None, dirty=False):
        pulls = iter(pulls or [[pr()], [pr()]])
        effects = []
        def command(*args):
            if fail and fail in args:
                raise subprocess.CalledProcessError(1, args)
            if args[:2] == ("gh", "api"):
                if any("/pulls?" in arg for arg in args):
                    return "\n".join(map(json.dumps, next(pulls)))
                return "\n".join(map(json.dumps, runs or []))
            if args[:3] == ("gh", "repo", "view"):
                return REPO
            if args[:3] == ("git", "remote", "get-url"):
                return remote or f"git@github.com:{REPO}.git"
            if args[:2] == ("git", "symbolic-ref"):
                return branch
            if args[:2] == ("git", "status"):
                return " M changed" if dirty and effects else ""
            if args[:2] == ("git", "rev-parse"):
                return "new" if mutation and effects else "local"
            self.fail(f"Unexpected command: {args}")
        with patch.object(publish, "command", command), patch.object(
                publish.subprocess, "run", side_effect=lambda args, **kw: effects.append(args)):
            result = publish.main()
        return result, effects

    def test_initial_push_and_completed_gate_publish_once_after_validation(self):
        for pulls in [[[], []], [[pr()], [pr()]]]:
            result, effects = self.run_publish(pulls=pulls, runs=[{"status": "completed", "conclusion": "failure"}])
            self.assertEqual(0, result)
            self.assertEqual([["pre-pr"], ["git", "push", "origin", "HEAD:refs/heads/topic"]], effects)

    def test_unfinished_gate_states_never_validate_or_push(self):
        for status in ["queued", "in_progress", "waiting", "pending", "requested"]:
            result, effects = self.run_publish(runs=[{"status": status, "id": 42}])
            self.assertEqual(75, result)
            self.assertEqual([], effects)

    def test_remote_head_change_close_or_new_pr_defers_after_validation(self):
        for pulls in [[[pr()], [pr("changed")]], [[pr()], []], [[], [pr()]]]:
            result, effects = self.run_publish(pulls=pulls)
            self.assertEqual(75, result)
            self.assertEqual([["pre-pr"]], effects)

    def test_local_mutation_defers_after_validation(self):
        result, effects = self.run_publish(mutation=True)
        self.assertEqual(75, result)
        self.assertEqual([["pre-pr"]], effects)

    def test_api_failure_never_pushes(self):
        result, effects = self.run_publish(fail="api")
        self.assertEqual(75, result)
        self.assertEqual([], effects)

    def test_pagination_covers_old_open_pr_and_active_run(self):
        with patch.object(publish, "command", side_effect=[json.dumps(pr()), "\n".join(map(json.dumps, [
                {"status": "completed"}, {"status": "in_progress", "id": 99}]))]) as command:
            with self.assertRaisesRegex(publish.Deferred, "99"):
                publish.inspect(REPO, "topic")
            for call in command.call_args_list:
                self.assertIn("--paginate", call.args)
                self.assertNotIn("--slurp", call.args)
                self.assertIn("--jq", call.args)

    def test_trunk_wrong_remote_and_ambiguous_pr_never_validate_or_push(self):
        for options in [{"branch": "trunk"}, {"remote": "https://github.com/other/repo.git"},
                        {"pulls": [[pr(), pr()]]}]:
            result, effects = self.run_publish(**options)
            self.assertEqual(75, result)
            self.assertEqual([], effects)

    def test_fork_and_deleted_fork_do_not_match_same_repo_branch(self):
        for repo in [{"full_name": "someone/fork"}, None]:
            fork = pr()
            fork["head"]["repo"] = repo
            with patch.object(publish, "command", return_value=json.dumps(fork)) as command:
                self.assertIsNone(publish.inspect(REPO, "topic"))
                self.assertEqual(1, command.call_count)

    def test_dirty_tree_after_validation_never_pushes(self):
        result, effects = self.run_publish(dirty=True)
        self.assertEqual(75, result)
        self.assertEqual([["pre-pr"]], effects)

    def test_validation_failure_never_pushes(self):
        with patch.object(publish, "inspect", return_value=None), patch.object(
                publish, "command", side_effect=["topic", "", REPO, f"git@github.com:{REPO}.git", "head"]), patch.object(
                publish.subprocess, "run", side_effect=subprocess.CalledProcessError(1, "pre-pr")) as run:
            self.assertEqual(75, publish.main())
            self.assertEqual(1, run.call_count)


if __name__ == "__main__":
    unittest.main()
