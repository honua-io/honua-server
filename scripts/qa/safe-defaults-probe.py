#!/usr/bin/env python3
"""Local defensive deployment probes. Never print secret values or raw runtime logs."""
import argparse
import base64
from datetime import datetime, timedelta, timezone
import json
import os
from pathlib import Path
import re
import secrets
import subprocess
import tempfile
import time
import urllib.error
import urllib.request

ROOT = Path(__file__).resolve().parents[2]


def compose(path, env, *args, env_file='/dev/null'):
    result = subprocess.run(
        ['docker', 'compose', '--env-file', str(env_file), '-f', str(path),
         *args, 'config', '--format', 'json'], env=env, capture_output=True, text=True)
    if result.returncode:
        return {'exit': result.returncode, 'missing_variables': sorted(set(
            re.findall(r'The "([A-Za-z0-9_]+)" variable is not set', result.stderr)))}
    services = json.loads(result.stdout)['services']
    return {'exit': 0, 'services': {name: {
        'ports': service.get('ports', []),
        'environment_names': sorted(service.get('environment', {})),
        'profile': service.get('environment', {}).get('ASPNETCORE_ENVIRONMENT'),
    } for name, service in services.items()}, '_raw': services}


def render():
    env = {k: v for k, v in os.environ.items() if k in ('PATH', 'HOME', 'DOCKER_HOST')}
    # Build-secret interpolation only; no authenticated build occurs here.
    env.update(GITHUB_ACTOR='qa', GITHUB_TOKEN='unused')
    default = compose(ROOT / 'docker-compose.yml', env, '--profile', 'minio', '--profile', 'console')
    default.pop('_raw', None)
    print(json.dumps({'case': 'quickstart', **default}))
    sample = compose(ROOT / 'docker-compose.yml', env, env_file=ROOT / '.env.production.example')
    sample_raw = sample.pop('_raw', {})
    quickstart_raw = compose(ROOT / 'docker-compose.yml', env).pop('_raw', {})
    fields = ['ConnectionStrings__DefaultConnection', 'ConnectionStrings__Redis',
              'Security__ConnectionEncryption__MasterKey', 'HONUA_ADMIN_PASSWORD']
    print(json.dumps({'case': 'exact-production-env-file', **sample,
        'same_as_quickstart': {field: sample_raw.get('honua', {}).get('environment', {}).get(field) ==
          quickstart_raw.get('honua', {}).get('environment', {}).get(field) for field in fields}}))
    sentinel = secrets.token_urlsafe(40)
    env.update(ASPNETCORE_ENVIRONMENT='Production',
               ConnectionStrings__DefaultConnection='Host=qa-db;Database=qa',
               ConnectionStrings__Redis='qa-redis:6379',
               Security__ConnectionEncryption__MasterKey=sentinel)
    production = compose(ROOT / 'docker-compose.yml', env)
    raw = production.pop('_raw', {})
    actual = raw.get('honua', {}).get('environment', {})
    print(json.dumps({'case': 'root-with-production-contract', **production,
        'respects_database_override': actual.get('ConnectionStrings__DefaultConnection') == env['ConnectionStrings__DefaultConnection'],
        'respects_redis_override': actual.get('ConnectionStrings__Redis') == env['ConnectionStrings__Redis'],
        'respects_master_key_override': actual.get('Security__ConnectionEncryption__MasterKey') == sentinel}))
    doc = (ROOT / 'docs/guides/deploy/docker-compose.md').read_text()
    shipped = re.search(r"cat > docker-compose.yml <<'EOF'\n(.*?)\nEOF", doc, re.S).group(1)
    with tempfile.TemporaryDirectory(prefix='honua-safe-compose-') as directory:
        path = Path(directory) / 'compose.yml'
        path.write_text(shipped)
        env = {k: v for k, v in env.items() if k in ('PATH', 'HOME', 'DOCKER_HOST')}
        env.update(HONUA_IMAGE='honua-server:qa', HONUA_STORAGE_VOLUME_NAME='qa-storage')
        result = compose(path, env)
        result.pop('_raw', None)
        print(json.dumps({'case': 'production-guide-missing-secrets', **result}))


def helm_render(chart, executable):
    doc = (ROOT / 'docs/guides/deploy/kubernetes.md').read_text()
    blocks = re.findall(r'```yaml\n(.*?)```', doc, re.S)
    with tempfile.TemporaryDirectory(prefix='honua-safe-helm-') as directory:
        cases = [('baseline', [])]
        for profile in ['prod', 'stage', 'dev']:
            cases.append((profile, ['-f', str(chart / f'values-{profile}.yaml')]))
        for index, block in enumerate(blocks[:2]):
            path = Path(directory) / f'guide-{index}.yaml'
            path.write_text(block)
            cases.append((f'guide-{index}', ['-f', str(path)]))
        for name, args in cases:
            if name != 'baseline':
                args += ['--set-string', 'image.digest=sha256:' + '0' * 64,
                         '--set-string', 'image.tag=', '--set-string', 'image.pullPolicy=IfNotPresent']
            result = subprocess.run([str(executable), 'template', 'honua', str(chart), *args],
                                    capture_output=True, text=True)
            summary = {'case': 'helm-' + name, 'exit': result.returncode,
                'service_types': re.findall(r'^  type: (ClusterIP|LoadBalancer|NodePort)', result.stdout, re.M),
                'preflight_required_keys': 'for var in ConnectionStrings__DefaultConnection HONUA_ADMIN_PASSWORD Security__ConnectionEncryption__MasterKey' in result.stdout}
            if result.returncode:
                # Guard messages only; do not request --debug manifest output.
                summary['error'] = result.stderr[:1000]
            elif name in ['prod', 'guide-0', 'guide-1']:
                import yaml
                for resource in yaml.safe_load_all(result.stdout):
                    if resource and resource.get('kind') == 'Job':
                        container = resource['spec']['template']['spec']['containers'][0]
                        command = container.get('command', []) + container.get('args', [])
                        check = subprocess.run(command, env={'PATH': os.environ['PATH']}, capture_output=True, text=True, timeout=10)
                        summary['missing_secrets_preflight_exit'] = check.returncode
                        summary['missing_secrets_preflight_detected'] = 'ConnectionStrings__DefaultConnection' in check.stdout + check.stderr
            print(json.dumps(summary), flush=True)


def runtime(dll, database=None):
    base = {k: v for k, v in os.environ.items() if k in ('PATH', 'HOME', 'DOTNET_ROOT', 'LD_LIBRARY_PATH')}
    base.update(ASPNETCORE_ENVIRONMENT='Production', DOTNET_ENVIRONMENT='Production',
                Kestrel__Endpoints__Http__Url='http://127.0.0.1:18943',
                Kestrel__Endpoints__Grpc__Url='http://127.0.0.1:18944',
                HostValidation__AllowedHosts__0='127.0.0.1',
                Cors__AllowedOrigins__0='https://qa.example.invalid',
                Logging__LogLevel__Default='Warning',
                Security__ConnectionEncryption__MasterKey=secrets.token_urlsafe(40))
    if database:
        base.update(ConnectionStrings__DefaultConnection=database,
                    HONUA_ADMIN_PASSWORD='Aa1!' + secrets.token_urlsafe(32))
    scenarios = {
        'community-control': {},
        'missing-db-and-auth': {'ConnectionStrings__DefaultConnection': '', 'HONUA_ADMIN_PASSWORD': ''},
        'missing-auth': {'HONUA_ADMIN_PASSWORD': ''},
        'missing-db': {'ConnectionStrings__DefaultConnection': ''},
        'missing-master-key': {'Security__ConnectionEncryption__MasterKey': ''},
        'weak-admin': {'HONUA_ADMIN_PASSWORD': secrets.token_hex(2)},
        'dev-auth-in-production': {'HONUA_DEV_AUTH': 'true'},
        'invalid-inline-license': {'Licensing__LicenseContent': '{invalid'},
        'missing-license-file': {'Licensing__LicensePath': '/tmp/nonexistent-safe-defaults-license.json'},
        'pro-dev-grant-in-production': {'Licensing__DevGrantEdition': 'Pro'},
        'enterprise-dev-grant-in-production': {'Licensing__DevGrantEdition': 'Enterprise'},
    }
    if database:
        from cryptography.hazmat.primitives.asymmetric.ed25519 import Ed25519PrivateKey
        from cryptography.hazmat.primitives.serialization import Encoding, PublicFormat
        key = Ed25519PrivateKey.generate()
        b64 = lambda value: base64.urlsafe_b64encode(value).decode().rstrip('=')
        base['Licensing__TrustedKeys__qa'] = 'base64url:' + b64(key.public_key().public_bytes(Encoding.Raw, PublicFormat.Raw))
        for edition in ['Community', 'Pro', 'Enterprise']:
            for state in ['valid', 'invalid-signature', 'expired']:
                payload = json.dumps({'schema': 'honua.license/v1', 'licenseId': 'local-qa',
                    'licensedTo': 'Local QA', 'edition': edition,
                    'issuedAt': (datetime.now(timezone.utc)-timedelta(days=2)).isoformat(),
                    'expiresAt': (datetime.now(timezone.utc)+timedelta(days=-1 if state == 'expired' else 1)).isoformat(),
                    'entitlements': ['editing.featureserver-edits'] if edition != 'Community' else []}).encode()
                signature = key.sign(payload)
                if state == 'invalid-signature':
                    signature = bytes([signature[0] ^ 255]) + signature[1:]
                envelope = json.dumps({'version': 1, 'keyId': 'qa', 'payload': b64(payload), 'signature': b64(signature)})
                scenarios[f'{edition.lower()}-{state}'] = {'Licensing__LicenseContent': envelope}
    for case, updates in scenarios.items():
        env = base | updates
        process = subprocess.Popen(['dotnet', str(dll)], cwd=ROOT / 'src/Honua.Server',
            env=env, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
        # Drain in a thread: keep all unredacted logs only in memory.
        import threading
        chunks = []
        reader = threading.Thread(target=lambda: chunks.append(process.stdout.read()), daemon=True)
        reader.start()
        statuses = {}
        try:
            for _ in range(600):
                if process.poll() is not None:
                    break
                try:
                    with urllib.request.urlopen('http://127.0.0.1:18943/healthz/live', timeout=.3) as response:
                        statuses['/healthz/live'] = response.status
                    break
                except (OSError, urllib.error.URLError):
                    time.sleep(.1)
            if statuses:
                for path in ['/healthz/ready', '/api/v1/admin/config', '/ogc/features', '/rest/services']:
                    try:
                        with urllib.request.urlopen('http://127.0.0.1:18943' + path, timeout=3) as response:
                            statuses[path] = response.status
                    except urllib.error.HTTPError as error:
                        statuses[path] = error.code
                    except (OSError, urllib.error.URLError):
                        statuses[path] = 'timeout-or-unavailable'
            license_state = None
            if statuses and env.get('HONUA_ADMIN_PASSWORD'):
                request = urllib.request.Request('http://127.0.0.1:18943/api/v1/admin/license/',
                    headers={'X-API-Key': env['HONUA_ADMIN_PASSWORD']})
                try:
                    with urllib.request.urlopen(request, timeout=5) as response:
                        data = json.load(response).get('data', {})
                        license_state = {key: data.get(key) for key in ['edition', 'isValid', 'validationState']}
                        license_state['active_paid_probe'] = any(e.get('key') == 'editing.featureserver-edits' and e.get('isActive') for e in data.get('entitlements', []))
                except (OSError, ValueError):
                    license_state = {'request_failed': True}
            exit_code = process.poll()
        finally:
            if process.poll() is None:
                process.terminate()
            try:
                process.wait(timeout=8)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait()
            reader.join(timeout=2)
        log = ''.join(chunks)
        markers = [s for s in ['Master key not configured', 'Master key must be at least 32',
            'Admin password must be at least 16', 'Admin password must contain',
            'Refusing to start', 'Development license grant', 'not allowed in Production',
            'HONUA_ADMIN_PASSWORD is required', 'ConnectionStrings__DefaultConnection is required',
            'Security__ConnectionEncryption__MasterKey is required', 'Configuration validation failed',
            'Connection string', 'No database connection', 'InvalidJson',
            'NoLicenseConfigured', 'FileNotFound', 'OutOfMemoryException'] if s.lower() in log.lower()]
        print(json.dumps({'case': case, 'exit_before_stop': exit_code,
            'http_statuses': statuses, 'log_markers': markers,
            'license_state': license_state,
            'exception_types': sorted(set(re.findall(r'\b([A-Za-z.]+Exception)\b', log)))}), flush=True)


def runtime_with_database(dll):
    name = 'gav-safe-defaults-' + secrets.token_hex(5)
    env = os.environ | {'POSTGRES_PASSWORD': secrets.token_urlsafe(32)}
    image = 'pgrouting/pgrouting:17-3.5-3.7.3'
    subprocess.run(['docker', 'run', '--rm', '-d', '--name', name,
        '-e', 'POSTGRES_PASSWORD', '-e', 'POSTGRES_USER=qa', '-e', 'POSTGRES_DB=qa',
        '-p', '127.0.0.1::5432', image], env=env, check=True, stdout=subprocess.DEVNULL)
    try:
        port = subprocess.check_output(['docker', 'port', name, '5432'], text=True).strip().split(':')[-1]
        for _ in range(120):
            ready = subprocess.run(['docker', 'exec', name, 'pg_isready', '-h', '127.0.0.1', '-U', 'qa', '-d', 'qa'], capture_output=True)
            if ready.returncode == 0:
                break
            time.sleep(.5)
        database = f"Host=127.0.0.1;Port={port};Database=qa;Username=qa;Password={env['POSTGRES_PASSWORD']};Timeout=5"
        runtime(dll, database)
    finally:
        subprocess.run(['docker', 'rm', '-f', name], stdout=subprocess.DEVNULL, check=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime', type=Path)
    parser.add_argument('--with-database', action='store_true')
    parser.add_argument('--helm-chart', type=Path)
    parser.add_argument('--helm-executable', type=Path, default=Path('helm'))
    args = parser.parse_args()
    if args.helm_chart:
        helm_render(args.helm_chart, args.helm_executable)
    elif args.runtime:
        (runtime_with_database if args.with_database else runtime)(args.runtime.resolve())
    else:
        render()
