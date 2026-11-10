#!/usr/bin/env python3
"""Retry only failed methods once, under the shard supervisor's original deadline."""
import argparse
from contextlib import suppress
import hashlib
import json
import os
import signal
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET

from flake_quarantine import exclude_filter, load_snapshot, merge_retry, read_cases


def call_test(command):
    # Own the test process group: foreground supervisors only signal this wrapper.
    with subprocess.Popen(command, start_new_session=os.name == 'posix') as child:
        def forward(signum, _frame):
            if child.poll() is not None:
                return
            if os.name != 'posix':
                child.send_signal(signum)
                return
            with suppress(ProcessLookupError):
                os.killpg(child.pid, signum)
            with suppress(subprocess.TimeoutExpired):
                child.wait(timeout=0.5)
            # Descendants can outlive the immediate child after cancellation.
            with suppress(ProcessLookupError):
                os.killpg(child.pid, signal.SIGKILL)
        previous = {sig: signal.signal(sig, forward) for sig in (signal.SIGTERM, signal.SIGINT)}
        try:
            return child.wait()
        finally:
            for sig, handler in previous.items():
                signal.signal(sig, handler)


def replace_option(command, option, value):
    result = list(command)
    result[result.index(option) + 1] = value
    return result


def snapshot_filter(command, project):
    path = os.environ.get('HONUA_TEST_QUARANTINE_FILE')
    if not path or os.environ.get('HONUA_TEST_QUARANTINE_OBSERVE') == 'true':
        return command
    entries = load_snapshot(path)
    expression = command[command.index('--filter') + 1] if '--filter' in command else ''
    expression = exclude_filter(expression, entries, project)
    if '--filter' in command:
        return replace_option(command, '--filter', expression)
    return [*command, '--filter', expression] if expression else command


def retry_command(command, failed, retry):
    methods = sorted({case[0] for case in failed})
    selected = '|'.join(f'FullyQualifiedName={method}' for method in methods)
    expression = command[command.index('--filter') + 1]
    command = replace_option(command, '--filter', f'({expression})&({selected})')
    return replace_option(command, '--logger', f'trx;LogFileName={retry.name}')


def record_candidates(options, failed):
    data = {'schema': 'honua.flake-candidates/v1', 'project': options.project,
            'tests': sorted({case[0] for case in failed}),
            'first_sha256': hashlib.sha256(options.first.read_bytes()).hexdigest(),
            'retry_sha256': hashlib.sha256(options.retry.read_bytes()).hexdigest()}
    options.candidates.write_text(json.dumps(data) + '\n')


def attempt_retry(options, command):
    failed = [case for case in read_cases(options.first) if case[2] == 'Failed']
    if not failed:
        return 1
    print(f'::warning::Retrying {len(failed)} failed case(s) once within the original shard deadline', flush=True)
    code = call_test(retry_command(command, failed, options.retry))
    if code != 0:
        return code
    merge_retry(options.first, options.retry, options.output)
    record_candidates(options, failed)
    return 0


def run(options):
    command = snapshot_filter(options.command, options.project)
    for path in (options.first, options.retry, options.candidates):
        path.unlink(missing_ok=True)
    code = call_test(command)
    if code != 1 or os.environ.get('HONUA_SERVER_TEST_RETRY_FAILED', 'true') != 'true':
        return code
    if not options.output.is_file():
        return code
    options.first.write_bytes(options.output.read_bytes())
    try:
        return attempt_retry(options, command)
    except (OSError, ValueError, KeyError, ET.ParseError) as error:
        print(f'::error::Retry evidence rejected: {error}', flush=True)
        return 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--project', required=True)
    parser.add_argument('--results', required=True, type=Path)
    parser.add_argument('--log', required=True)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    options = parser.parse_args()
    if options.command[0:1] == ['--']:
        options.command = options.command[1:]
    options.output = options.results / f'{options.log}.trx'
    options.first = options.results / f'{options.log}.first-attempt.xml'
    options.retry = options.results / f'{options.log}.retry-attempt.xml'
    options.candidates = options.results / f'{options.log}.flake-candidates.json'
    return run(options)


def exit_with_status(result):
    if result < 0 and os.name == 'posix':
        if -result not in (signal.SIGKILL, signal.SIGSTOP):
            signal.signal(-result, signal.SIG_DFL)
        os.kill(os.getpid(), -result)
    sys.exit(result)


if __name__ == '__main__':
    exit_with_status(main())
