#!/usr/bin/env python3
"""Private lane driver. AWS responses and credentials stay in a private temporary directory."""
import base64
from datetime import datetime, timedelta, timezone
import hashlib
import json
import os
from pathlib import Path
import re
import signal
import subprocess
import sys
import tempfile
from urllib.parse import urlencode, urlsplit

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests/seed/client-compat-v1.sql"
# The administrator the lane authenticates as is the bootstrap credential the server compares
# x-api-key against (ApiKeyAuthenticationHandler), so HONUA_LAMBDA_CERT_ADMIN_KEY only opens
# the door while it equals what this variable resolves to on the function under test.
ADMIN_CREDENTIAL_VARIABLE = "HONUA_ADMIN_PASSWORD"
SECRET_REFERENCE_PREFIX = "aws:secretsmanager:"
ADMIN_API_KEYS = "/api/v1/admin/api-keys"
# The authorization assertion needs a principal that authenticates and holds no admin rights. An
# API key lives in Redis (or process-local memory), so losing its store turns a bootstrap key's 403 into a 401,
# and a 401 certifies a missing credential rather than authorization. The lane therefore mints its
# own scoped principal for the run and revokes it at teardown. The bootstrap secret remains an
# optional override for one release so existing bootstraps keep working.
DENIED_KEY_VARIABLE = "HONUA_LAMBDA_CERT_DENIED_KEY"
DENIED_KEY_PERMISSIONS = ["read:layers"]
# An abandoned credential is a standing one: this bound retires the minted key anyway if the run
# dies between the mint and the revoke. It is far longer than a certification run.
DENIED_KEY_LIFETIME_HOURS = 2


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def fingerprint(value):
    return "sha256:" + hashlib.sha256(value.encode()).hexdigest()


def aws(*args):
    result = subprocess.run(["aws", *args, "--output", "json"], capture_output=True, text=True)
    # Never echo CLI diagnostics: configuration responses can contain credentials.
    require(result.returncode == 0, f"AWS {args[0]} {args[1]} failed")
    return json.loads(result.stdout or "{}")


def write_json(path, data):
    Path(path).write_text(json.dumps(data))


def config(function, qualifier=None):
    args = ["lambda", "get-function", "--function-name", function]
    if qualifier:
        args += ["--qualifier", qualifier]
    return aws(*args)


def inputs():
    override = override_denied_key()
    require(not use_denied_key_override() or override,
            "Denied-key override was requested but HONUA_LAMBDA_CERT_DENIED_KEY is missing")
    require(not override or override != os.environ["HONUA_LAMBDA_CERT_ADMIN_KEY"],
            "Denied principal must differ from the administrator")
    function = os.environ["REALAWS_CERT_LAMBDA_FUNCTION"]
    alias = os.environ["REALAWS_CERT_LAMBDA_ALIAS"]
    require(re.fullmatch(r"honua-cert-cert-[A-Za-z0-9_-]+", function), "Standing function outside cert namespace")
    require(re.fullmatch(r"[A-Za-z][A-Za-z0-9_-]*", alias), "Invalid cert alias")
    write = urlsplit(os.environ["HONUA_LAMBDA_WRITE_BASE_URL"])
    demo = urlsplit(os.environ["HONUA_DEMO_BASE_URL"])
    for url in (write, demo):
        require(url.scheme == "https" and url.hostname and not url.username and not url.password
                and not url.query and not url.fragment and url.path in ("", "/"), "Expected an HTTPS base URL")
    require(write.hostname.lower().rstrip(".") != demo.hostname.lower().rstrip("."), "REFUSED: write base URL equals demo read URL host")
    actual = aws("lambda", "get-function-url-config", "--function-name", function, "--qualifier", alias)
    require(actual["FunctionUrl"].rstrip("/").lower() == os.environ["HONUA_LAMBDA_WRITE_BASE_URL"].rstrip("/").lower(),
            "Write URL does not belong to the standing cert alias")
    return function, alias


def prepare(directory):
    function, alias = inputs()
    current = config(function)
    cfg = current["Configuration"]
    require(cfg["PackageType"] == "Image", "Standing function must use an image")
    require(cfg["Architectures"] == [os.environ["HONUA_LAMBDA_ARCHITECTURE"]], "Standing architecture mismatch")
    variables = cfg["Environment"]["Variables"]
    require(variables.get("HONUA_SKIP_MIGRATIONS", "false").lower() == "false", "Standing function skips migrations: noProof")
    require(variables.get("ConnectionStrings__DefaultConnection"), "Cert PostGIS connection is missing")
    if not use_denied_key_override():
        # Configuration alone cannot prove the Redis multiplexer was selected at runtime. After
        # minting, certify also requires the standing alias to see the candidate's exact key record.
        for target_variables in (variables, config(function, alias)["Configuration"]["Environment"]["Variables"]):
            require(any(str(value).strip() for name, value in target_variables.items()
                        if name.lower() in ("connectionstrings__redis", "aspire__stackexchange__redis__connectionstring")),
                    "Per-run denial keys require shared Redis configuration on candidate and standing alias")
    # Authentication is cloned, not supplied: the lane never injects a credential of its own, so a
    # standing environment without this variable can only answer every administrative assertion with
    # 401. Refuse by name before anything is mirrored or created, rather than after the deploy.
    # Whitespace is not a credential: ResolveAdminPasswordAsync treats an all-whitespace value as
    # unconfigured, so it must fail here rather than 401 every administrative assertion later.
    require(variables.get(ADMIN_CREDENTIAL_VARIABLE, "").strip(),
            f"Standing function carries no {ADMIN_CREDENTIAL_VARIABLE}: the cert admin key cannot be accepted")
    require(cfg["VpcConfig"].get("SubnetIds") and cfg["VpcConfig"].get("SecurityGroupIds"), "Cert PostGIS VPC is missing")
    # The standing function already reaches the cert stack's private PostGIS and resolves its secrets.
    # Clone its configuration, including authentication; never substitute a loopback connection.
    write_json(directory / "environment.json", {"Variables": variables})
    write_json(directory / "vpc.json", {k: cfg["VpcConfig"][k] for k in ("SubnetIds", "SecurityGroupIds")})
    write_json(directory / "standing.json", current)


def admin_key():
    # Runtime-only secret; not written to a receipt, stdout, or a repository path.
    return os.environ["HONUA_LAMBDA_CERT_ADMIN_KEY"]


def use_denied_key_override():
    return os.environ.get("HONUA_LAMBDA_CERT_USE_DENIED_KEY_OVERRIDE", "").lower() == "true"


def override_denied_key():
    # Optional for one release so an existing bootstrap keeps working. Blank is absent, not a key.
    value = os.environ.get(DENIED_KEY_VARIABLE, "")
    return value if use_denied_key_override() and value.strip() else ""


# The denied principal this run is actually sending, and what it knows about that record. A minted
# key carries an id, so its state can be read back from the server; an override is opaque by design.
_denied = {"source": "override", "value": "", "id": None, "name": None}


def denied_key():
    return _denied["value"] or override_denied_key()


def runtime_secrets():
    # Every credential this run holds, including the one it minted for itself.
    return tuple(value for value in (os.environ["HONUA_LAMBDA_CERT_ADMIN_KEY"],
                                     os.environ.get(DENIED_KEY_VARIABLE, ""), _denied["value"]) if value)


# A whole key is not the only thing worth refusing to print. Filtering and truncating a diagnostic
# can leave a key that carried an excluded character, or one longer than the cap, behind as a
# normalized or truncated fragment that no longer equals the secret. Treat any run of this many
# consecutive key characters as the key itself: server-authored refusal details are fixed English
# constants, so a collision this long with a real credential does not happen by accident.
SECRET_FRAGMENT = 12


def leaks(text, secret):
    if len(secret) <= SECRET_FRAGMENT:
        return secret in text
    return any(secret[index:index + SECRET_FRAGMENT] in text
               for index in range(len(secret) - SECRET_FRAGMENT + 1))


def redacted(value, limit):
    # Server-authored diagnostics only: strip everything outside a narrow printable set and cap the
    # length, then drop the whole field outright if either runtime key shows through. The comparison
    # runs against the ORIGINAL text as well as the filtered one, because filtering first is exactly
    # what would let a key survive the check in a form that no longer matches it.
    original = str(value)
    text = re.sub(r"[^A-Za-z0-9 ._:/-]", "", original)[:limit]
    for secret in runtime_secrets():
        if leaks(original, secret) or leaks(text, secret):
            return "[redacted]"
    return text


# Payload format 2.0 folds repeated response headers into one comma-joined value, so the two
# challenges the server appends arrive as `ApiKey realm="...", header="...", Basic realm="..."`.
# Match scheme tokens rather than splitting on commas: `header=` and `charset=` are parameters of
# the scheme before them, not schemes of their own.
CHALLENGE_SCHEME = re.compile(r"(?:^|,)\s*([A-Za-z][A-Za-z0-9._-]{0,31})(?=\s+[A-Za-z]|\s*$)")


def challenge_schemes(headers):
    schemes = []
    for name, value in (headers or {}).items():
        if str(name).lower() != "www-authenticate":
            continue
        for entry in (value if isinstance(value, list) else [value]):
            for scheme in CHALLENGE_SCHEME.findall(str(entry)):
                if scheme not in schemes:
                    schemes.append(scheme)
    summary = "+".join(schemes) or "none"
    return "[redacted]" if any(leaks(summary, secret) for secret in runtime_secrets()) else summary


def admin_credential_state(function):
    # Names only. An environment value is a credential and never leaves the function, but whether
    # the variable is there at all - and whether it is a Secrets Manager reference the function
    # resolves per request, or an inline value - is what separates "this deployment has no
    # administrator" from "it has one this key no longer matches".
    try:
        variables = config(function)["Configuration"]["Environment"]["Variables"]
    except (RuntimeError, KeyError, ValueError, OSError):
        return "unreadable", "unknown"
    value = variables.get(ADMIN_CREDENTIAL_VARIABLE, "")
    if not value.strip():
        return "absent", "none"
    return "present", ("secretsmanager-reference"
                       if value.lower().startswith(SECRET_REFERENCE_PREFIX) else "inline")


# Lambda answers an initialization failure, a handler exception and a timeout the same way at the
# API: HTTP 200 with FunctionError set, the reason only in the invocation's response payload, and
# the platform's own account of it only in the log tail. Live run 25 (34305710517) stopped on that
# bare "Lambda invocation failed" and the job log said nothing else, so the run could not name the
# side that had failed - the per-run candidate or the standing alias - nor whether the function had
# died initializing, thrown while serving, or run out of time. Every one of those has a different
# owner, so say which, from the invoke's own answer.
INIT_ERROR_TYPE = re.compile(r"^(Runtime[.]|Init)", re.IGNORECASE)
TIMED_OUT = re.compile(r"task timed out", re.IGNORECASE)
# The platform's own verdict on the initialization that ran in this environment.
FAILED_INIT_REPORT = re.compile(r"^INIT_REPORT\b.*Status: (?:error|timeout)", re.MULTILINE)
INVOKE_LOG_TAIL_LINES = 20


def invoke_target(function):
    # Which side of the certification the invocation landed on, never which function: the standing
    # function is a fingerprint everywhere else in this evidence, and the alias qualifier the lane
    # appends is exactly what separates the two targets it invokes.
    return "standing-alias" if ":" in function else "candidate"


def invoke_log_tail(meta):
    # --log-type Tail already carries this invocation's own log back with the response, so the
    # initialization that failed is in hand without a CloudWatch query, a delivery wait, or a
    # permission on another function's log group.
    try:
        return base64.b64decode(meta.get("LogResult") or "").decode(errors="replace")
    except (ValueError, TypeError):
        return ""


def invoke_failure_kind(payload, tail):
    if FAILED_INIT_REPORT.search(tail):
        return "init"
    error_type = str(payload.get("errorType", ""))
    if INIT_ERROR_TYPE.match(error_type):
        return "init"
    if TIMED_OUT.search(str(payload.get("errorMessage", ""))):
        return "timeout"
    return "handler" if error_type or payload.get("errorMessage") else "unknown"


def report_invoke_failure(target, path, meta, response):
    # The error document Lambda writes in place of the HTTP response, when there is one: a dry-run
    # status or a throttled call leaves the previous invocation's file, or none at all.
    try:
        payload = json.loads(Path(response).read_text() or "null")
    except (OSError, ValueError):
        payload = None
    payload = payload if isinstance(payload, dict) else {}
    tail = invoke_log_tail(meta)
    print(f"serving-invoke: phase={_phase} target={target} path={path} "
          f"status={meta.get('StatusCode')} "
          f"executed-version={redacted(meta.get('ExecutedVersion') or '', 20) or 'none'} "
          f"function-error={redacted(meta.get('FunctionError') or '', 40) or 'none'} "
          f"kind={invoke_failure_kind(payload, tail)} "
          f"error-type={redacted(payload.get('errorType', ''), 60) or 'none'} "
          f"error-message={redacted(payload.get('errorMessage', ''), 200) or 'none'}", file=sys.stderr)
    # The tail is server- and platform-authored text about this exact request, and it is the only
    # place a failed initialization's own output appears. It goes through the same redaction as
    # every other echoed diagnostic, line by line and bounded.
    for line in [entry for entry in tail.splitlines() if entry.strip()][-INVOKE_LOG_TAIL_LINES:]:
        print(f"serving-invoke-log: {redacted(line, 200)}", file=sys.stderr)


def invoke(function, path, *, method="GET", query=None, body=None, json_body=None,
           authenticated=True, api_key=None, expected_version=None):
    headers = {"accept": "application/json", "host": urlsplit(os.environ["HONUA_LAMBDA_WRITE_BASE_URL"]).netloc}
    if api_key is not None or authenticated:
        headers["x-api-key"] = api_key if api_key is not None else admin_key()
    if body is not None:
        headers["content-type"] = "application/x-www-form-urlencoded"
    elif json_body is not None:
        headers["content-type"] = "application/json"
    event = {"version": "2.0", "routeKey": f"{method} {path}", "rawPath": path,
             "rawQueryString": urlencode(query or {}), "headers": headers,
             "requestContext": {"http": {"method": method, "path": path, "protocol": "HTTP/1.1",
                                         "sourceIp": "127.0.0.1", "userAgent": "honua-lambda-cert"}},
             "isBase64Encoded": False}
    if body is not None:
        event["body"] = urlencode(body)
    elif json_body is not None:
        event["body"] = json.dumps(json_body)
    with tempfile.TemporaryDirectory(prefix="honua-cert-invoke-") as temporary:
        payload, response = Path(temporary) / "payload.json", Path(temporary) / "response.json"
        write_json(payload, event)
        meta = aws("lambda", "invoke", "--function-name", function, "--cli-binary-format", "raw-in-base64-out",
                   "--log-type", "Tail", "--payload", f"file://{payload}", str(response))
        if meta.get("StatusCode") != 200 or meta.get("FunctionError"):
            report_invoke_failure(invoke_target(function), path, meta, response)
            require(False, "Lambda invocation failed")
        if expected_version:
            require(meta.get("ExecutedVersion") == expected_version, "Alias invocation executed the wrong version")
        result = json.loads(response.read_text())
        raw = result.get("body", "")
        if result.get("isBase64Encoded"):
            raw = base64.b64decode(raw).decode()
        try:
            parsed = json.loads(raw)
            # A JSON string literal (including "") is a nonempty HTTP body. Preserve its bytes
            # so the zero-body 403 assertion cannot accept a JSON-encoded empty string.
            if isinstance(parsed, str):
                parsed = raw
        except json.JSONDecodeError:
            parsed = raw
        return result.get("statusCode"), parsed, meta, result.get("headers")


def ok(function, path, *, expect=200, **kwargs):
    status, body, _, headers = invoke(function, path, **kwargs)
    if not (status == expect and isinstance(body, dict) and "error" not in body):
        # Diagnosable without leaking: the path is a fixed lane constant, the
        # status is a number, and only a short, alphanumeric error code/title
        # from the body is echoed (never the body, headers, or a key). Live run
        # 34117861856 failed here with nothing but the message, and the cause
        # (400 "Invalid Host header") took a manual probe to find.
        code = ""
        if isinstance(body, dict):
            err = body.get("error")
            if isinstance(err, dict):
                code = redacted(err.get("code", ""), 40)
            code = code or redacted(body.get("title", body.get("type", "")), 60)
        print(f"serving-assertion: phase={_phase} path={path} status={status} expected={expect} "
              f"body-kind={body_kind(body)} error={code or 'none'}", file=sys.stderr)
        if status == 401:
            # Every administrative assertion authenticates as the bootstrap administrator, and the
            # title of that refusal is "Unauthorized" whatever the cause. Run 21 (34222614774)
            # stopped here and neither the deployed configuration nor the challenge was in the log,
            # so telling "this function has no administrator" apart from "it has one this key no
            # longer matches" took the standing configuration and a manual probe. Say both in the
            # run that failed: the credential variable by NAME (never its value), and the scheme
            # that issued the challenge with the server's own fixed refusal detail.
            presence, source = admin_credential_state(function)
            detail = redacted(body.get("detail", ""), 120) if isinstance(body, dict) else ""
            print(f"serving-401: variable={ADMIN_CREDENTIAL_VARIABLE} presence={presence} "
                  f"source={source} challenge={challenge_schemes(headers)} "
                  f"detail={detail or 'none'}", file=sys.stderr)
        require(False, "Serving HTTP assertion failed")
    return body


_phase = "deployed"


def set_phase(name):
    global _phase
    _phase = name


def body_kind(body):
    if body == "":
        return "empty"
    return "json" if isinstance(body, (dict, list)) else "text"


# What the status alone already settles about the denied principal: 401 is a principal the server
# did not recognize, 403 is one it recognized and refused, and 200 is one it recognized and served.
AUTHENTICATED = {200: "yes", 401: "no", 403: "yes"}


def denied_record_status(function):
    # Only a key this run minted has a record it can ask about by id; an override is opaque by
    # design, and no plaintext key can be mapped back to its row.
    if not _denied["id"]:
        return "unknown"
    try:
        status, body, _, _ = invoke(function, ADMIN_API_KEYS + "/" + _denied["id"] + "/effective-permissions")
        if status == 404:
            return "missing"
        if status != 200 or not isinstance(body, dict):
            return "unreadable"
        return redacted((body.get("data") or {}).get("status", ""), 20) or "unknown"
    except Exception:  # noqa: BLE001 - a diagnostic must never replace the assertion it explains
        return "unreadable"


def report_denied(function, status, body, headers):
    # Run 23 (34243173689) failed this assertion with nothing but its message, so the run could not
    # say which of two opposite things had happened: the scoped key was gone (401), or the
    # server served the admin surface to a non-admin principal (200 with records, honua-server#4386).
    # The status, the shape of the body and the challenge separate them in the run that failed.
    records = str(len(body)) if isinstance(body, list) else "0" if body == "" else "unknown"
    if isinstance(body, dict):
        # Counted, never quoted: a leaked record is evidence, and its contents are not the lane's
        # to print. A document carrying none of these keys carries no records at all.
        records = "0"
        for key in ("data", "features", "records", "layers", "items", "apiKeys"):
            if key in body:
                records = str(len(body[key])) if isinstance(body[key], (list, dict)) else "unknown"
                break
    print(f"serving-403: phase={_phase} principal={_denied['source']} "
          f"key={_denied['name'] or DENIED_KEY_VARIABLE} status={status} body-kind={body_kind(body)} "
          f"authenticated={AUTHENTICATED.get(status, 'unknown')} challenge={challenge_schemes(headers)} "
          f"records={records} record={denied_record_status(function)}",
          file=sys.stderr)


def mint_denied_key(function, record):
    """Mint this run's own scoped principal through the admin API-key endpoint.

    The lane already holds the administrator, so the denial assertion does not have to depend on a
    key some earlier bootstrap left in the API-key store.
    """
    set_phase("denied-key-mint")
    name = "honua-cert-denied-" + os.environ["GITHUB_RUN_ID"] + "-" + os.environ["GITHUB_RUN_ATTEMPT"]
    # Named before the call, so teardown can still find the record a lost create response left behind.
    _denied["name"] = record["name"] = name
    expires = datetime.now(timezone.utc) + timedelta(hours=DENIED_KEY_LIFETIME_HOURS)
    issued = ok(function, ADMIN_API_KEYS, expect=201, method="POST", json_body={
        "name": name, "permissions": list(DENIED_KEY_PERMISSIONS),
        "expiresAt": expires.strftime("%Y-%m-%dT%H:%M:%SZ")}).get("data") or {}
    key, metadata = issued.get("key"), issued.get("apiKey") or {}
    require(isinstance(key, str) and key and key != admin_key(),
            "Minted denial principal must be a key of its own")
    require(isinstance(metadata.get("id"), str) and metadata["id"], "Minted denial key has no record id")
    require(metadata.get("name") == name and metadata.get("status") == "active"
            and list(metadata.get("permissions") or []) == DENIED_KEY_PERMISSIONS,
            "Minted denial key is not this run's active read:layers principal")
    _denied.update(source="minted", value=key, id=metadata["id"])
    record["created"] = True


def verify_shared_denied_key(target, record):
    set_phase("denied-key-shared-store")
    status, body, _, _ = invoke(target, ADMIN_API_KEYS + "/" + _denied["id"] + "/effective-permissions")
    effective = body.get("data") if isinstance(body, dict) else None
    require(status == 200 and isinstance(effective, dict)
            and effective.get("id") == _denied["id"] and effective.get("name") == _denied["name"]
            and effective.get("status") == "active" and effective.get("canAuthenticate") is True
            and effective.get("permissions") == DENIED_KEY_PERMISSIONS,
            "Standing alias cannot see the minted denial principal: require a shared Redis-backed API-key store")
    record["sharedStoreVerified"] = True


def retire_through(target, record):
    name = _denied["name"]
    listed = ok(target, ADMIN_API_KEYS).get("data") or []
    # The name carries this run's id and attempt and only this lane ever mints it, so every match
    # is this run's own: revoke all of them rather than refusing an ambiguity that would leave a
    # live credential behind.
    owned = [entry for entry in listed if isinstance(entry, dict) and entry.get("name") == name]
    for entry in owned:
        if entry.get("status") != "revoked":
            revoked = ok(target, ADMIN_API_KEYS + "/" + str(entry.get("id")) + "/revoke",
                         method="POST", json_body={}).get("data") or {}
            require(revoked.get("status") == "revoked", "Denial key was not revoked")
    if _denied["id"]:
        # Say it from the server's own view of the record rather than from the call that revoked it.
        effective = ok(target, ADMIN_API_KEYS + "/" + _denied["id"] + "/effective-permissions").get("data") or {}
        require(effective.get("status") == "revoked" and effective.get("canAuthenticate") is False,
                "Revoked denial key still reports it can authenticate")
        record["canAuthenticate"] = effective["canAuthenticate"]
    remaining = [entry for entry in (ok(target, ADMIN_API_KEYS).get("data") or [])
                 if isinstance(entry, dict) and entry.get("name") == name and entry.get("status") != "revoked"]
    record["activeAfterTeardown"] = len(remaining)
    require(not remaining, "Denial key is still active after teardown")
    record["revoked"] = True


def retire_denied_key(targets, record):
    """Revoke this run's key by its unique name, including one a lost create response left behind.

    The row outlives the functions this run creates, so it must not outlive the run: the candidate
    is tried first, and the standing alias after it, because a candidate that cannot serve is
    exactly the run that would otherwise leave the credential behind.
    """
    set_phase("denied-key-retire")
    for target in targets:
        try:
            retire_through(target, record)
            return
        except Exception:  # noqa: BLE001 - whatever one target answered, the other still has to try
            continue
    require(False, "Denial key could not be revoked on any target")


def smoke(function, expected_version=None):
    common = {"expected_version": expected_version}
    migration = ok(function, "/api/v1/admin/observability/migrations", **common)
    require(migration.get("status") == "succeeded" and migration.get("isReady") is True
            and migration.get("isFailed") is False and migration.get("planAvailable") is True
            and migration.get("upgradeRequired") is False and migration.get("pendingScripts") == [],
            "Migrations not applied: noProof")
    path = "/rest/services/test_service/FeatureServer/0"
    # Fixed, versioned fixture; never infer the expected count from the target's answer.
    count = ok(function, path + "/query", query={"f": "json", "where": "1=1", "returnCountOnly": "true"}, **common)
    require(type(count.get("count")) is int and count["count"] == 10, "Fixture row count must equal 10")
    rows = ok(function, path + "/query", query={"f": "json", "where": "1=1", "outFields": "name", "returnGeometry": "false"}, **common)
    expected_names = sorted(["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "lambda"])
    require(sorted(feature["attributes"]["name"] for feature in rows.get("features", [])) == expected_names,
            "Fixture records do not match client-compat-v1")
    # The anonymous principal has no admin rights. This documented 401 must contain no records.
    status, denial, _, _ = invoke(function, ADMIN_API_KEYS, authenticated=False, **common)
    require(status == 401 and isinstance(denial, dict), "Authorization denial must be HTTP 401")
    require(denial.get("status") == 401 and denial.get("type") == "https://honua.io/problems/admin",
            "Authorization refusal body is not the documented error")
    require(not any(key in denial for key in ("data", "features", "records", "layers", "items", "apiKeys")),
            "Authorization denial leaked records")
    require(all(not isinstance(value, (dict, list)) for value in denial.values()),
            "Authorization denial contains structured records")
    # This is authorization, not merely a missing-credential challenge: this run's own read:layers
    # key must authenticate and receive the documented 403.
    status, denial, _, headers = invoke(function, ADMIN_API_KEYS, api_key=denied_key(), **common)
    if not (status == 403 and denial == ""):
        report_denied(function, status, denial, headers)
    require(status == 403 and denial == "", "Scoped principal must receive an empty HTTP 403 (zero records)")
    write_path = "/rest/services/test_service/FeatureServer/10"
    marker = "honua-certrun-" + os.environ["GITHUB_RUN_ID"] + "-" + os.environ["GITHUB_RUN_ATTEMPT"]
    # Resolve by our unique marker during finally too, including ambiguous create responses.
    query = {"f": "json", "where": f"name = '{marker}'", "outFields": "*", "returnGeometry": "false"}
    require(ok(function, write_path + "/query", query=query, **common).get("features") == [], "Write marker already exists")
    cleanup_needed = True
    try:
        added = ok(function, write_path + "/addFeatures", method="POST", body={"f": "json", "features": json.dumps([
            {"attributes": {"name": marker}, "geometry": {"x": -122.42, "y": 37.76, "spatialReference": {"wkid": 4326}}}])}, **common)
        results = added.get("addResults", [])
        require(len(results) == 1 and results[0].get("success") is True and type(results[0].get("objectId")) is int,
                "Create assertion failed")
        object_id = results[0]["objectId"]
        read = ok(function, write_path + "/query", query=query, **common).get("features", [])
        require(len(read) == 1 and read[0]["attributes"].get("name") == marker
                and read[0]["attributes"].get("objectid") == object_id, "Write readback assertion failed")
        deleted = ok(function, write_path + "/deleteFeatures", method="POST", body={"f": "json", "objectIds": str(object_id)}, **common)
        results = deleted.get("deleteResults", [])
        require(len(results) == 1 and results[0].get("success") is True and results[0].get("objectId") == object_id,
                "Delete assertion failed")
        require(ok(function, write_path + "/query", query=query, **common).get("features") == [], "Deleted row still served")
        cleanup_needed = False
    finally:
        if cleanup_needed:
            # Only this run's marker; never a broad fixture reset or delete by untrusted ID.
            result = ok(function, write_path + "/deleteFeatures", method="POST", body={"f": "json", "where": query["where"]}, **common)
            require(all(item.get("success") is True for item in result.get("deleteResults", [])), "Write cleanup failed")
            require(ok(function, write_path + "/query", query=query, **common).get("features") == [], "Write cleanup left records")
    return {"result": "pass", "migrations": {"status": "succeeded", "pendingScripts": 0, "upgradeRequired": False},
            "fixture": {"name": "client-compat-v1", "sha256": fingerprint(FIXTURE.read_text()), "expectedRows": 10, "actualRows": 10, "namesVerified": True},
            "write": {"createdRows": 1, "readBackRows": 1, "deletedRows": 1, "remainingRows": 0, "distinctWriteUrl": True},
            "authorization": {"principal": "scoped-api-key", "principalSource": _denied["source"],
                              "operation": "GET /api/v1/admin/api-keys", "expectedStatus": 403, "actualStatus": 403, "records": 0, "anonymousStatus": 401},
            "executedVersion": expected_version or "$LATEST"}


def backend(action, function, alias, previous, candidate):
    driver = ROOT / "scripts/cloud/lambda-deploy-driver/bin/Release/net10.0/LambdaDeployDriver.dll"
    result = subprocess.run(["dotnet", str(driver), action, function, alias, previous, candidate, os.environ["AWS_REGION"]],
                            capture_output=True, text=True)
    require(result.returncode == 0, f"Deploy backend {action} failed")
    value = json.loads(result.stdout)
    require(value.get("version") == (previous if action == "rollback" else candidate), "Backend version assertion failed")


def alias_state(function, alias, expected=None):
    state = aws("lambda", "get-alias", "--function-name", function, "--name", alias)
    require(not state.get("RoutingConfig", {}).get("AdditionalVersionWeights"), "Standing alias has weighted traffic")
    require(re.fullmatch(r"[1-9][0-9]*", state.get("FunctionVersion", "")), "Standing alias needs a published version")
    if expected:
        require(state["FunctionVersion"] == expected, "Alias version assertion failed")
    return state["FunctionVersion"]


def certify(directory, ephemeral, digest):
    function, alias = inputs()
    proof = {"result": "noProof", "candidateDigest": digest.split("@")[-1],
             "deniedKey": {"source": "override" if override_denied_key() else "minted",
                           "permissions": list(DENIED_KEY_PERMISSIONS), "name": None, "created": False,
                           "revoked": False, "canAuthenticate": None, "activeAfterTeardown": None,
                           "sharedStoreVerified": False}}
    write_json(directory / "serving.json", proof)
    teardown_errors = []
    try:
        if proof["deniedKey"]["source"] == "minted":
            mint_denied_key(ephemeral, proof["deniedKey"])
            verify_shared_denied_key(function + ":" + alias, proof["deniedKey"])
        serve(directory, function, alias, ephemeral, digest, proof)
    finally:
        if proof["deniedKey"]["source"] == "minted":
            try:
                retire_denied_key((ephemeral, function + ":" + alias), proof["deniedKey"])
            except Exception as error:  # noqa: BLE001 - recorded, then raised as a lane failure
                teardown_errors.append(error)
                proof["result"] = "noProof"
        write_json(directory / "serving.json", proof)
    require(not teardown_errors, "Denial key teardown failed")


def serve(directory, function, alias, ephemeral, digest, proof):
    set_phase("deployed")
    proof["deployed"] = smoke(ephemeral)
    previous = alias_state(function, alias)
    target = function + ":" + alias
    # Prove the baseline can serve before publishing anything to the standing function.
    set_phase("baseline")
    proof["baseline"] = smoke(target, previous)
    original = json.loads((directory / "standing.json").read_text())
    latest = config(function)
    require(latest["Configuration"]["RevisionId"] == original["Configuration"]["RevisionId"], "Standing function changed during certification")
    versions_before = {v["Version"] for v in aws("lambda", "list-versions-by-function", "--function-name", function)["Versions"]}
    ownership = "honua-cert-run=" + os.environ["GITHUB_RUN_ID"] + "-" + os.environ["GITHUB_RUN_ATTEMPT"]
    candidate = None
    proof["alias"] = {"beforeVersion": previous, "afterVersion": None, "rollbackVersion": None}
    proof["teardown"] = {"candidateVersionDeleted": False, "standingLatestRestored": False}
    cleanup_errors = []

    def clean(action):
        try:
            action()
        except Exception as error:
            cleanup_errors.append(error)

    changed = False
    rollback_needed = False
    try:
        changed = True
        update = aws("lambda", "update-function-code", "--function-name", function, "--image-uri", digest,
                     "--revision-id", latest["Configuration"]["RevisionId"])
        aws("lambda", "wait", "function-updated-v2", "--function-name", function)
        deployed = config(function)
        require(deployed["Code"]["ResolvedImageUri"] == digest, "Standing candidate digest mismatch")
        published = aws("lambda", "publish-version", "--function-name", function,
                        "--revision-id", deployed["Configuration"]["RevisionId"],
                        "--code-sha256", update["CodeSha256"], "--description", ownership)
        candidate = published["Version"]
        require(re.fullmatch(r"[1-9][0-9]*", candidate) and candidate not in versions_before, "Candidate is not a new published version")
        require(config(function, candidate)["Code"]["ResolvedImageUri"] == digest, "Published candidate digest mismatch")
        rollback_needed = True  # Set BEFORE the call: an SDK timeout may follow a successful shift.
        backend("shift", function, alias, previous, candidate)
        proof["alias"]["afterVersion"] = alias_state(function, alias, candidate)
        set_phase("candidate")
        proof["candidate"] = smoke(target, candidate)
    finally:
        if changed and candidate is None:
            # A publish can succeed even when its response is lost. Recover only a newly
            # created version bearing this run's unique description, never a pre-existing one.
            def recover_candidate():
                nonlocal candidate
                versions = aws("lambda", "list-versions-by-function", "--function-name", function)["Versions"]
                owned = [v["Version"] for v in versions if v["Version"] not in versions_before and v.get("Description") == ownership]
                require(len(owned) <= 1, "Ambiguous candidate ownership")
                candidate = owned[0] if owned else None
            clean(recover_candidate)
        if rollback_needed:
            clean(lambda: backend("rollback", function, alias, previous, candidate))
            def observe_rollback():
                proof["alias"]["rollbackVersion"] = alias_state(function, alias, previous)
            clean(observe_rollback)
            def verify_rollback():
                set_phase("rollback")
                proof["rollback"] = smoke(target, previous)
            clean(verify_rollback)
        if changed:
            def restore_latest():
                # Restore pre-existing $LATEST code as well as alias routing. Configuration was never edited.
                current = config(function)
                require(current["Code"]["ResolvedImageUri"] in (digest, original["Code"]["ResolvedImageUri"]),
                        "Standing latest drifted; refusing to overwrite unrelated code")
                aws("lambda", "update-function-code", "--function-name", function,
                    "--image-uri", original["Code"]["ResolvedImageUri"], "--revision-id", current["Configuration"]["RevisionId"])
                aws("lambda", "wait", "function-updated-v2", "--function-name", function)
                require(config(function)["Code"]["ResolvedImageUri"] == original["Code"]["ResolvedImageUri"], "Standing latest restoration failed")
                proof["teardown"]["standingLatestRestored"] = True
            clean(restore_latest)
        if candidate:
            def delete_candidate():
                require(candidate not in versions_before, "Refusing deletion of a pre-existing version")
                owned = config(function, candidate)
                require(owned["Configuration"].get("Description") == ownership and owned["Code"]["ResolvedImageUri"] == digest,
                        "Candidate version ownership mismatch")
                alias_state(function, alias, previous)
                aliases = aws("lambda", "list-aliases", "--function-name", function)["Aliases"]
                require(all(a["FunctionVersion"] != candidate and candidate not in a.get("RoutingConfig", {}).get("AdditionalVersionWeights", {}) for a in aliases),
                        "Candidate still referenced; refusing version deletion")
                aws("lambda", "delete-function", "--function-name", function, "--qualifier", candidate)
                versions = aws("lambda", "list-versions-by-function", "--function-name", function)["Versions"]
                require(all(v["Version"] != candidate for v in versions), "Candidate version remains after deletion")
                proof["teardown"]["candidateVersionDeleted"] = True
            clean(delete_candidate)
        proof["candidateVersion"] = candidate
        write_json(directory / "serving.json", proof)
    require(not cleanup_errors, "Alias rollback or candidate teardown failed")
    proof.update(result="pass", alias={"beforeVersion": previous, "afterVersion": candidate, "rollbackVersion": previous},
                 teardown={"candidateVersionDeleted": True, "standingLatestRestored": True})
    write_json(directory / "serving.json", proof)


if __name__ == "__main__":
    def interrupted(_signum, _frame):
        raise RuntimeError("Certification interrupted")

    signal.signal(signal.SIGTERM, interrupted)
    signal.signal(signal.SIGINT, interrupted)
    try:
        if sys.argv[1] == "invoke-failure":
            # The shell stage invokes the candidate directly for its cold-start evidence. Report its
            # failures through the same classifier and the same redaction rather than a second,
            # drifting copy of both in bash.
            set_phase("cold-start-evidence")
            report_invoke_failure(sys.argv[2], sys.argv[3], json.loads(Path(sys.argv[4]).read_text() or "{}"),
                                  sys.argv[5])
        elif sys.argv[1] == "prepare":
            prepare(Path(sys.argv[2]))
        elif sys.argv[1] == "certify":
            certify(Path(sys.argv[2]), sys.argv[3], sys.argv[4])
        else:
            raise RuntimeError("Unknown lane command")
    except RuntimeError as error:
        print(f"{error}; serving noProof", file=sys.stderr)
        sys.exit(1)
    except Exception:  # noqa: BLE001
        # Raw API exceptions/bodies can contain secrets. Receipts remain noProof on any error.
        print("Lambda certification assertion failed; serving noProof", file=sys.stderr)
        sys.exit(1)
