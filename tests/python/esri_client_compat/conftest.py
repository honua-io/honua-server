"""Identity tests use a genuine owned TLS listener and worker-scoped catalog."""
from __future__ import annotations

import os
from pathlib import Path
import secrets
import ssl

import httpx
import pytest

from shared.identity_tls import SecretRedactor, free_loopback_ports, owned_identity_tls
from shared.server import HonuaServer


@pytest.fixture(scope='session')
def identity_transport(request):
    # An external fixture requires its own verified HTTPS identity endpoint and
    # explicit layer binding. Never redirect local test credentials to it silently.
    if os.getenv('HONUA_TEST_BASE_URL'):
        url = os.environ.get('HONUA_ESRI_IDENTITY_URL', '').rstrip('/')
        if not url.startswith('https://'):
            pytest.fail('External identity coverage requires HONUA_ESRI_IDENTITY_URL with verified HTTPS.')
        ca = os.environ.get('HONUA_ESRI_IDENTITY_CA_FILE')
        environment = {'HONUA_ADMIN_PASSWORD': os.environ.get('HONUA_ADMIN_PASSWORD', '')}
        if not environment['HONUA_ADMIN_PASSWORD']:
            pytest.fail('External identity coverage requires HONUA_ADMIN_PASSWORD.')
        for name in ('HONUA_ESRI_IDENTITY_SERVICE', 'HONUA_ESRI_IDENTITY_LAYER'):
            if not os.environ.get(name):
                pytest.fail(f'External identity coverage requires {name}.')
        context = ssl.create_default_context(cafile=ca) if ca else ssl.create_default_context()
        redactor = SecretRedactor(environment)
        yield {'url': url, 'verify': context, 'environment': environment, 'redactor': redactor,
               'http_url': None, 'ca': ca, 'server': None}
        return

    # Lazy request keeps ordinary suites unchanged. This extra server only exists
    # when identity coverage is collected; each worker uses its isolated schema.
    postgis = request.getfixturevalue('postgis')
    worker_schema = request.getfixturevalue('worker_schema')
    inherited_endpoints = [name for name in os.environ
                           if name.lower().startswith(('kestrel__endpoints__', 'aspnetcore_kestrel__endpoints__'))]
    if inherited_endpoints:
        pytest.fail('Owned identity fixture cannot inherit unreviewed Kestrel endpoints; clear endpoint configuration for this child test invocation.')
    parent = Path(os.environ.get('HONUA_TEST_ARTIFACT_ROOT',
                  'C:/Users/mike/honua-io/python-identity-tests' if os.name == 'nt'
                  else os.environ.get('RUNNER_TEMP', '/tmp'))) / 'honua-owned-identity'
    with owned_identity_tls(parent) as tls:
        http_port, https_port = free_loopback_ports()
        url = f'https://localhost:{https_port}'
        environment = {
            'HONUA_ADMIN_PASSWORD': os.environ.get('HONUA_ADMIN_PASSWORD') or secrets.token_urlsafe(36),
            'HONUA_DEV_AUTH': 'false', 'HONUA_DEV_AUTH_ALLOW_BYPASS': 'false',
            'Kestrel__Endpoints__Http__Url': f'http://127.0.0.1:{http_port}',
            'Kestrel__Endpoints__Http__Protocols': 'Http1',
            'Kestrel__Endpoints__IdentityHttps__Url': f'https://127.0.0.1:{https_port}',
            'Kestrel__Endpoints__IdentityHttps__Protocols': 'Http1',
            'Kestrel__Endpoints__IdentityHttps__Certificate__Path': str(tls.certificate),
            'Kestrel__Endpoints__IdentityHttps__Certificate__KeyPath': str(tls.private_key),
            'PUBLIC_BASE_URL': url,
            'Cors__AllowedOrigins__0': os.getenv('HONUA_ESRI_PROBE_ORIGIN', 'http://localhost:3000'),
        }
        redactor = SecretRedactor(environment)
        connection_string = postgis.get_npgsql_connection_string(search_path=f'{worker_schema}, public')
        redactor.add(connection_string)
        server = HonuaServer(connection_string=connection_string, port=http_port, environment=environment,
            log_redactor=redactor)
        try:
            server.start(timeout=float(os.getenv('HONUA_TEST_TIMEOUT', '120')))
            context = tls.context()
            with httpx.Client(verify=context, trust_env=False, timeout=10) as client:
                response = client.get(url + '/healthz/live')
                assert response.status_code == 200
            yield {'url': url, 'verify': context, 'environment': environment, 'redactor': redactor,
                   'http_url': f'http://127.0.0.1:{http_port}', 'ca': str(tls.ca), 'server': server}
        finally:
            server.stop()


@pytest.fixture
def identity_client(identity_transport):
    with httpx.Client(base_url=identity_transport['url'], verify=identity_transport['verify'],
                      trust_env=False, timeout=30) as client:
        yield client


@pytest.fixture
def identity_query_path(test_service_id, test_layer_id):
    service = os.environ['HONUA_ESRI_IDENTITY_SERVICE'] if os.getenv('HONUA_TEST_BASE_URL') else test_service_id
    layer = os.environ['HONUA_ESRI_IDENTITY_LAYER'] if os.getenv('HONUA_TEST_BASE_URL') else test_layer_id
    from urllib.parse import quote
    return f'/rest/services/{quote(service, safe="")}/FeatureServer/{int(layer)}/query'
