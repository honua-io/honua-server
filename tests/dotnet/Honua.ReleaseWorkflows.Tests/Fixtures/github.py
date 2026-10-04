#!/usr/bin/env python3
"""Offline GitHub interleaving: A precedes dispatch B, unrelated C wins latest."""
import io
import json
import os
import sys
import zipfile
from pathlib import Path

root = Path(os.environ['FIXTURE_ROOT'])
scenario = os.environ['FIXTURE_SCENARIO']
args = sys.argv[1:]
with (root / 'calls').open('a') as calls:
    calls.write(' '.join(args) + '\n')

run = {'id': 202, 'run_attempt': 1, 'event': 'workflow_dispatch',
       'path': '.github/workflows/compat.yml', 'head_sha': 'b' * 40,
       'repository': {'full_name': 'honua-io/client'},
       'status': 'completed', 'conclusion': 'failure' if scenario == 'overlap' else 'success',
       'html_url': 'https://github.com/honua-io/client/actions/runs/202'}
for field, value in {'wrong-event': ('event', 'schedule'),
                     'wrong-sha': ('head_sha', 'c' * 40),
                     'wrong-workflow': ('path', '.github/workflows/other.yml'),
                     'wrong-repo': ('repository', {'full_name': 'honua-io/other'}),
                     'wrong-run': ('id', 303),
                     'rerun': ('run_attempt', 2),
                     'timeout': ('status', 'in_progress')}.items():
    if scenario == field:
        run[value[0]] = value[1]

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
    endpoint = next(arg for arg in args[1:] if arg.startswith('repos/')).split('?', 1)[0]
    if endpoint.endswith('/dispatches'):
        payload = json.load(sys.stdin)
        assert payload['return_run_details'] is True
        assert payload['ref'] == 'trunk'
        (root / 'payload').write_text(json.dumps(payload))
        response = {'workflow_run_id': 202,
                    'html_url': 'https://github.com/honua-io/client/actions/runs/202'}
        if scenario == 'missing-dispatch-id':
            response = {}
        elif scenario == 'ambiguous-dispatch':
            response = [response, {'workflow_run_id': 303}]
        print(json.dumps(response))
    elif '/commits/' in endpoint:
        print(json.dumps({'sha': 'b' * 40}))
    elif endpoint.endswith('/artifacts'):
        artifact = {'id': 404, 'name': 'release-suite-receipt', 'expired': False,
                    'workflow_run': {'id': 202, 'head_sha': 'b' * 40}}
        artifacts = [artifact]
        if scenario == 'missing-receipt':
            artifacts = []
        elif scenario == 'ambiguous-receipt':
            artifacts.append({**artifact, 'id': 405})
        elif scenario == 'expired-receipt':
            artifact['expired'] = True
        print(json.dumps([{'artifacts': artifacts}]))
    elif endpoint.endswith('/404/zip'):
        (root / 'artifact-read').touch()
        image = (json.loads((root / 'payload').read_text())['inputs'].get('server_image', ''))
        receipt = {'id': 'sdk-test', 'owningRepo': 'honua-io/client', 'workflow': 'compat.yml',
                   'runId': 202, 'runAttempt': 1, 'headSha': 'b' * 40,
                   'image': image, 'imageDigest': image.rsplit('@', 1)[-1]}
        for name, (field, value) in {
                'receipt-wrong-digest': ('imageDigest', 'sha256:' + 'c' * 64),
                'receipt-wrong-image': ('image', 'ghcr.io/other/server@' + receipt['imageDigest']),
                'receipt-wrong-run': ('runId', 303),
                'receipt-wrong-attempt': ('runAttempt', 2),
                'receipt-wrong-suite': ('id', 'another-suite'),
                'receipt-wrong-sha': ('headSha', 'c' * 40)}.items():
            if scenario == name:
                receipt[field] = value
        archive = io.BytesIO()
        with zipfile.ZipFile(archive, 'w') as output:
            output.writestr('release-suite-receipt.json', json.dumps(receipt))
        sys.stdout.buffer.write(archive.getvalue())
    elif '/actions/runs/202' in endpoint:
        if scenario == 'rerun-after-receipt' and (root / 'artifact-read').exists():
            run['run_attempt'] = 2
        print(json.dumps(run))
    else:
        raise AssertionError(endpoint)
else:
    raise AssertionError(args)
