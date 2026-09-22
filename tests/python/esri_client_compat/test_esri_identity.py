"""Esri identity contract against the worker catalog over verified TLS."""
from __future__ import annotations

import os
from pathlib import Path
import ssl

import httpx
import pytest


def _generate_token(client, transport, **extra: str) -> str:
    payload = {
        'username': os.environ.get('HONUA_ADMIN_USERNAME', 'admin'),
        'password': transport['environment']['HONUA_ADMIN_PASSWORD'],
        'client': 'requestip', 'f': 'json', **extra,
    }
    response = client.post('/sharing/rest/generateToken', data=payload)
    assert response.status_code == 200, 'Token issuance must succeed over verified HTTPS.'
    body = response.json()
    assert isinstance(body.get('token'), str) and body['token'], 'Token issuance returned no usable token.'
    transport['redactor'].add(body['token'])
    return body['token']


def test_rest_info_advertises_portal_token_service(identity_client) -> None:
    response = identity_client.get('/rest/info', params={'f': 'json'})
    assert response.status_code == 200
    auth_info = response.json().get('authInfo', {})
    assert auth_info.get('isTokenBasedSecurity') is True
    token_url = auth_info.get('tokenServicesUrl', '')
    assert token_url.startswith('https://')
    assert token_url.endswith('/sharing/rest/generateToken')


def test_invalid_portal_token_uses_esri_498_envelope(identity_client, identity_query_path) -> None:
    # Prove the worker service/layer resolves before challenging its credential.
    control = identity_client.get(identity_query_path,
        params={'where': '1=1', 'returnCountOnly': 'true', 'f': 'json'})
    assert control.status_code == 200
    assert isinstance(control.json().get('count'), int), 'Identity fixture layer did not resolve.'
    response = identity_client.get(identity_query_path,
        params={'where': '1=1', 'f': 'json', 'token': 'not-a-real-token'})
    assert response.json().get('error', {}).get('code') == 498


def test_x_esri_authorization_is_allowed_by_cors_preflight(identity_client, identity_query_path) -> None:
    response = identity_client.options(identity_query_path, headers={
        'Origin': os.getenv('HONUA_ESRI_PROBE_ORIGIN', 'http://localhost:3000'),
        'Access-Control-Request-Method': 'POST',
        'Access-Control-Request-Headers': 'content-type,x-esri-authorization',
    })
    assert response.status_code == 204
    assert 'x-esri-authorization' in response.headers.get('access-control-allow-headers', '').lower()


def test_oauth_userinfo_is_served(identity_client, identity_transport) -> None:
    token = _generate_token(identity_client, identity_transport)
    response = identity_client.get('/sharing/rest/oauth2/userinfo', params={'f': 'json', 'token': token})
    assert response.status_code == 200
    body = response.json()
    assert body.get('error', {}).get('code') != 404
    assert body.get('sub') or body.get('username')


def test_services_directory_does_not_duplicate_name_and_type(identity_client, identity_transport) -> None:
    token = _generate_token(identity_client, identity_transport, expiration='60')
    response = identity_client.get('/rest/services', params={'f': 'json', 'token': token})
    assert response.status_code == 200
    entries = [(service['name'], service['type']) for service in response.json()['services']]
    assert len(entries) == len(set(entries))


def test_identity_owned_listener_retains_https_requirement(identity_transport) -> None:
    http_url = identity_transport['http_url']
    if http_url is None:
        # External HTTPS-only deployments have no owned HTTP listener to test.
        # This is an external fixture capability; the default CI always runs it.
        pytest.skip('External fixture has no owned HTTP listener; default local CI executes this control.')
    with httpx.Client(trust_env=False, timeout=10) as client:
        response = client.post(http_url + '/sharing/rest/generateToken', data={
            'username': 'admin', 'password': identity_transport['environment']['HONUA_ADMIN_PASSWORD'],
            'client': 'requestip', 'f': 'json'})
    assert response.status_code == 403
    assert 'token' not in response.json()


def test_identity_owned_listener_rejects_untrusted_ca(identity_transport) -> None:
    if identity_transport['server'] is None:
        pytest.skip('External certificate may use public trust; default local CI executes owned-CA rejection.')
    with httpx.Client(verify=ssl.create_default_context(), trust_env=False, timeout=10) as client:
        with pytest.raises(httpx.ConnectError) as error:
            client.get(identity_transport['url'] + '/healthz/live')
    cause = error.value
    while cause is not None and not isinstance(cause, ssl.SSLCertVerificationError):
        cause = cause.__cause__ or cause.__context__
    assert isinstance(cause, ssl.SSLCertVerificationError), 'Expected a certificate verification failure.'


def test_arcgis_python_username_password_login_and_feature_query(
        identity_transport, identity_query_path, identity_client, monkeypatch) -> None:
    pytest.importorskip('arcgis')  # Existing optional SDK dependency, unrelated to the three fixed regressions.
    from arcgis.features import FeatureLayer
    from arcgis.gis import GIS
    if identity_transport['ca']:
        monkeypatch.setenv('REQUESTS_CA_BUNDLE', identity_transport['ca'])
    gis = GIS(identity_transport['url'], username=os.environ.get('HONUA_ADMIN_USERNAME', 'admin'),
              password=identity_transport['environment']['HONUA_ADMIN_PASSWORD'], verify_cert=True)
    layer = FeatureLayer(identity_transport['url'] + identity_query_path.removesuffix('/query'), gis=gis)
    result = layer.query(where='1=1', out_fields='*', return_geometry=False)
    control = identity_client.get(identity_query_path,
        params={'where': '1=1', 'returnCountOnly': 'true', 'f': 'json'})
    assert control.status_code == 200
    assert len(result.features) == control.json()['count']


def test_query_token_is_not_written_to_request_log(identity_client, identity_query_path) -> None:
    log_path = os.getenv('HONUA_ESRI_LOG_FILE')
    if not log_path:
        pytest.skip('Existing log-source prerequisite: set HONUA_ESRI_LOG_FILE to the unmodified captured server log.')
    marker = 'ESRI_PROBE_LOG_MARKER'
    identity_client.get(identity_query_path, params={
        'where': '1=1', 'outFields': '*', 'returnGeometry': 'false', 'token': marker, 'f': 'json'})
    # Do not inspect fixture-redacted buffers: that would make the assertion tautological.
    assert marker not in Path(log_path).read_text(encoding='utf-8')
