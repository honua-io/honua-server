#!/usr/bin/env python3
"""Local defensive deployment probes. Never print secret values or raw runtime logs."""
import argparse
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


def compose(path, env, *args):
    result = subprocess.run(
        ['docker', 'compose', '--env-file', '/dev/null', '-f', str(path),
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


def runtime(dll):
    base = {k: v for k, v in os.environ.items() if k in ('PATH', 'HOME', 'DOTNET_ROOT', 'LD_LIBRARY_PATH')}
    base.update(ASPNETCORE_ENVIRONMENT='Production', DOTNET_ENVIRONMENT='Production',
                Kestrel__Endpoints__Http__Url='http://127.0.0.1:18943',
                Kestrel__Endpoints__Grpc__Url='http://127.0.0.1:18944',
                HostValidation__AllowedHosts__0='127.0.0.1',
                Cors__AllowedOrigins__0='https://qa.example.invalid',
                Logging__LogLevel__Default='Warning',
                Security__ConnectionEncryption__MasterKey=secrets.token_urlsafe(40))
    scenarios = {
        'missing-db-and-auth': {},
        'missing-master-key': {'Security__ConnectionEncryption__MasterKey': ''},
        'weak-admin': {'HONUA_ADMIN_PASSWORD': secrets.token_hex(2)},
        'dev-auth-in-production': {'HONUA_DEV_AUTH': 'true'},
        'invalid-inline-license': {'Licensing__LicenseContent': '{invalid'},
        'missing-license-file': {'Licensing__LicensePath': '/tmp/nonexistent-safe-defaults-license.json'},
        'pro-dev-grant-in-production': {'Licensing__DevGrantEdition': 'Pro'},
        'enterprise-dev-grant-in-production': {'Licensing__DevGrantEdition': 'Enterprise'},
    }
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
            for _ in range(100):
                if process.poll() is not None:
                    break
                try:
                    with urllib.request.urlopen('http://127.0.0.1:18943/healthz/live', timeout=.3) as response:
                        statuses['/healthz/live'] = response.status
                    break
                except (OSError, urllib.error.URLError):
                    time.sleep(.1)
            if statuses:
                for path in ['/healthz/ready', '/api/v1/admin/config', '/api/v1/license', '/ogc/features', '/rest/services']:
                    try:
                        with urllib.request.urlopen('http://127.0.0.1:18943' + path, timeout=3) as response:
                            statuses[path] = response.status
                    except urllib.error.HTTPError as error:
                        statuses[path] = error.code
                    except (OSError, urllib.error.URLError):
                        statuses[path] = 'timeout-or-unavailable'
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
            'Connection string', 'No database connection', 'InvalidJson',
            'NoLicenseConfigured', 'FileNotFound', 'OutOfMemoryException'] if s.lower() in log.lower()]
        print(json.dumps({'case': case, 'exit_before_stop': exit_code,
            'http_statuses': statuses, 'log_markers': markers,
            'exception_types': sorted(set(re.findall(r'\b([A-Za-z.]+Exception)\b', log)))}), flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--runtime', type=Path)
    args = parser.parse_args()
    if args.runtime:
        runtime(args.runtime.resolve())
    else:
        render()
