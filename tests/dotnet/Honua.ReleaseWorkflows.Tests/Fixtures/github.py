#!/usr/bin/env python3
"""Offline GitHub interleaving: A precedes dispatch B, unrelated C wins latest."""
import json
import os
import sys
from pathlib import Path

root = Path(os.environ['FIXTURE_ROOT'])
args = sys.argv[1:]
with (root / 'calls').open('a') as calls:
    calls.write(' '.join(args) + '\n')

if args[:2] == ['workflow', 'run']:
    (root / 'dispatched').touch()
elif args[:2] == ['run', 'list']:
    print(303 if (root / 'dispatched').exists() else 101)
elif args[:2] == ['run', 'watch']:
    pass
elif args[:2] == ['run', 'view']:
    run_id = args[2]
    print(json.dumps({'conclusion': 'success' if run_id == '303' else 'failure',
                      'url': f'https://github.com/honua-io/client/actions/runs/{run_id}',
                      'headSha': 'b' * 40}))
elif args[0] == 'api':
    endpoint = next(arg for arg in args[1:] if arg.startswith('repos/'))
    if endpoint.endswith('/dispatches'):
        payload = json.load(sys.stdin)
        assert payload['return_run_details'] is True
        assert payload['ref'] == 'trunk'
        print(json.dumps({'workflow_run_id': 202,
                          'html_url': 'https://github.com/honua-io/client/actions/runs/202'}))
    elif '/commits/' in endpoint:
        print(json.dumps({'sha': 'b' * 40}))
    elif '/actions/runs/202' in endpoint:
        print(json.dumps({'id': 202, 'run_attempt': 1, 'event': 'workflow_dispatch',
                          'path': '.github/workflows/compat.yml', 'head_sha': 'b' * 40,
                          'repository': {'full_name': 'honua-io/client'},
                          'status': 'completed', 'conclusion': 'failure',
                          'html_url': 'https://github.com/honua-io/client/actions/runs/202'}))
    else:
        raise AssertionError(endpoint)
else:
    raise AssertionError(args)
