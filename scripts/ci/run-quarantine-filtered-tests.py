#!/usr/bin/env python3
"""Apply the validated quarantine to foundation and lean-gate duplicate coverage."""
import importlib.util
from pathlib import Path
import sys

spec = importlib.util.spec_from_file_location('retry_runner', Path(__file__).with_name('retry-failed-shard-tests.py'))
runner = importlib.util.module_from_spec(spec)
spec.loader.exec_module(runner)


def main():
    command = sys.argv[1:]
    if command[:2] != ['dotnet', 'test'] or not command[2:]:
        raise ValueError('Expected dotnet test and an explicit project')
    return runner.call_test(runner.snapshot_filter(command, command[2]))


if __name__ == '__main__':
    runner.exit_with_status(main())
