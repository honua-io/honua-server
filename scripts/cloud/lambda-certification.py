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
from urllib.parse import unquote, urlencode, urlsplit

ROOT = Path(__file__).resolve().parents[2]
FIXTURE = ROOT / "tests/seed/client-compat-v1.sql"
# Resolve the same reference used by ApiKeyAuthenticationHandler at runtime.
ADMIN_CREDENTIAL_VARIABLE = "HONUA_ADMIN_PASSWORD"
SECRET_REFERENCE_PREFIX = "aws:secretsmanager:"
ADMIN_API_KEYS = "/api/v1/admin/api-keys"
# The scratch-layer write is a Pro surface: GeoServices FeatureServer editing is gated on the
# `editing.featureserver-edits` entitlement (FeatureServerEditsHandler), so an unlicensed function
# refuses addFeatures. The envelope reaches the function through these variables (honua-iac
# aws-serverless), and the server's own verdict on what it made of them is at LICENSE_STATUS.
LICENSE_CONTENT_VARIABLE = "Licensing__LicenseContentSecretRef"
LICENSE_TRUSTED_KEY_PREFIX = "Licensing__TrustedKeys__"
LICENSE_STATUS = "/api/v1/admin/license/status"
FEATURESERVER_EDITS_ENTITLEMENT = "editing.featureserver-edits"
# The GeoServices write operations that funnel through FeatureServerEditsHandler, which is where the
# entitlement is enforced for the whole surface. A 402 anywhere else is LicenseOperationMiddleware
# refusing the deployment license outright - a different owner - so the entitlement is never claimed
# for one: an expired license blocking `/api/v1/admin/observability/migrations` must not be reported
# as an edit-entitlement failure.
FEATURESERVER_EDIT_OPERATIONS = ("/addFeatures", "/updateFeatures", "/deleteFeatures",
                                 "/applyEdits", "/calculate")
# The entitlement refusal is HTTP 402, and the GeoServices formatter carries that status
# through as the body code (StandardErrorResponseFormatter). Either is the same denial.
PAYMENT_REQUIRED = 402
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
_admin_key = None


class SecretReadDenied(RuntimeError):
    pass


def require(condition, message):
    if not condition:
        raise RuntimeError(message)


def fingerprint(value):
    return "sha256:" + hashlib.sha256(value.encode()).hexdigest()


def aws(*args):
    result = subprocess.run(["aws", *args, "--output", "json"], capture_output=True, text=True)
    # Never echo CLI diagnostics: configuration responses can contain credentials.
    if (result.returncode and args[:2] == ("secretsmanager", "get-secret-value")
            and re.search(r"\(AccessDenied(?:Exception)?\)", result.stderr)):
        raise SecretReadDenied("Secrets Manager read denied")
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
    admin_key(current)
    # The standing function already reaches the cert stack's private PostGIS and resolves its secrets.
    # Clone its configuration, including authentication; never substitute a loopback connection.
    write_json(directory / "environment.json", {"Variables": variables})
    write_json(directory / "vpc.json", {k: cfg["VpcConfig"][k] for k in ("SubnetIds", "SecurityGroupIds")})
    write_json(directory / "standing.json", current)


def secret_reference(reference):
    # Match AwsSecretsManagerResolver's prefix and URI-escaped version options.
    require(isinstance(reference, str) and reference.lower().startswith(SECRET_REFERENCE_PREFIX),
            "HONUA_ADMIN_PASSWORD must name an aws:secretsmanager: reference when no override is set")
    secret_id, _, query = reference[len(SECRET_REFERENCE_PREFIX):].partition("?")
    arn = re.fullmatch(r"arn:(aws(?:-[a-z-]+)?):secretsmanager:([a-z0-9-]+):(\d{12}):secret:([A-Za-z0-9/_+=.@-]+)", secret_id)
    require(arn or re.fullmatch(r"[A-Za-z0-9/_+=.@-]+", secret_id),
            "Invalid Secrets Manager identifier in HONUA_ADMIN_PASSWORD")
    options = {}
    for part in query.split("&"):
        key, separator, value = part.strip().partition("=")
        if not separator:
            continue
        require(not re.search(r"%(?![0-9a-fA-F]{2})", value), "Malformed secret version option encoding")
        value = unquote(value, errors="strict")
        if key.lower() in ("versionstage", "versionid"):
            options["--version-stage" if key.lower() == "versionstage" else "--version-id"] = value
    args = ["--secret-id", secret_id]
    if arn:
        args += ["--region", arn[2]]
    for key, value in options.items():
        if value.strip():
            args += [key, value]
    return secret_id, args


def secret_arn_pattern(secret_id, current):
    if secret_id.startswith("arn:"):
        return secret_id if re.search(r"-[A-Za-z0-9]{6}$", secret_id) else secret_id + "-??????"
    # Names need the ARN's six-character Secrets Manager suffix, never a stack-wide wildcard.
    function_arn = current["Configuration"]["FunctionArn"]
    match = re.fullmatch(r"arn:(aws(?:-[a-z-]+)?):lambda:([a-z0-9-]+):(\d{12}):function:[A-Za-z0-9_-]+", function_arn)
    require(match, "Cannot derive the secret ARN pattern from the standing function ARN")
    return f"arn:{match[1]}:secretsmanager:{os.environ['AWS_REGION']}:{match[3]}:secret:{secret_id}-??????"


def mask_secret(value):
    # Workflow command escaping prevents newlines or percent sequences from injecting log commands.
    # Outside Actions there is no masking consumer, so never emit the credential at all.
    if os.environ.get("GITHUB_ACTIONS") == "true":
        escaped = value.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
        print(f"::add-mask::{escaped}", flush=True)


def admin_key(current=None):
    global _admin_key
    if _admin_key is not None:
        return _admin_key
    value = os.environ.get("HONUA_LAMBDA_CERT_ADMIN_KEY", "")
    if not value:
        current = current or config(os.environ["REALAWS_CERT_LAMBDA_FUNCTION"])
        reference = current["Configuration"]["Environment"]["Variables"].get(ADMIN_CREDENTIAL_VARIABLE)
        secret_id, args = secret_reference(reference)
        try:
            response = aws("secretsmanager", "get-secret-value", *args)
        except SecretReadDenied:
            raise RuntimeError("STOP: OIDC role needs secretsmanager:GetSecretValue on "
                               + secret_arn_pattern(secret_id, current)
                               + "; update the honua-iac CertificationStackSecretsRead grant") from None
        value = response.get("SecretString")
        if not isinstance(value, str) and isinstance(response.get("SecretBinary"), str):
            value = base64.b64decode(response["SecretBinary"], validate=True).decode("utf-8")
    require(isinstance(value, str) and value.strip(), "Cert admin credential resolved empty or missing")
    mask_secret(value)
    require(value != override_denied_key(), "Denied principal must differ from the administrator")
    _admin_key = value
    return value


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


# The lane clones the standing function's whole environment onto the candidate, so a value the
# lane never chose - an inline connection string, a token - can come back inside a server-authored
# diagnostic. Those values are secrets of this run too, and every echoed diagnostic has to compare
# against them, exactly as the shell stage's create-function reporter already does.
# A declared reference is not one of them: `aws:secretsmanager:<arn>` and `env:<name>` are pointers
# the lane already reports publicly by kind (admin_credential_state prints
# `source=secretsmanager-reference`), and treating them as secrets would drop every diagnostic line
# that so much as names Secrets Manager - which is exactly the line an initialization failure
# resolving a secret would print. Values below the fragment window are configuration flags
# ("false", "1024"), not credentials, and matching them would redact by coincidence.
# A whole key is not the only thing worth refusing to print. Filtering and truncating a diagnostic
# can leave a key that carried an excluded character, or one longer than the cap, behind as a
# normalized or truncated fragment that no longer equals the secret. Treat any run of this many
# consecutive key characters as the key itself: server-authored refusal details are fixed English
# constants, so a collision this long with a real credential does not happen by accident.
SECRET_FRAGMENT = 12


CLONED_SECRET_REFERENCES = ("aws:secretsmanager:", "env:")
_cloned = []


def load_cloned_secrets(directory):
    try:
        variables = json.loads((Path(directory) / "environment.json").read_text()).get("Variables") or {}
    except (OSError, ValueError, AttributeError):
        return
    _cloned[:] = [value for value in variables.values()
                  if isinstance(value, str) and len(value.strip()) > SECRET_FRAGMENT
                  and not value.strip().lower().startswith(CLONED_SECRET_REFERENCES)]


def runtime_secrets():
    # Every credential this run holds: the ones it was given, the one it minted for itself, and
    # every cloned environment value that could be one.
    return tuple(value for value in (_admin_key, os.environ.get("HONUA_LAMBDA_CERT_ADMIN_KEY", ""),
                                     os.environ.get(DENIED_KEY_VARIABLE, ""), _denied["value"],
                                     *_cloned) if value)


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


def join_details(details):
    # The GeoServices envelope writes `details` as an array. Join it into one bounded field with a
    # separator the redaction filter keeps (it strips commas and semicolons), so the server's own
    # explanation - the upgrade message, the offending field, the entitlement key - stays readable.
    if isinstance(details, list):
        return " :: ".join(str(item) for item in details)
    return "" if details is None else str(details)


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


def license_configuration_state(function):
    # Names and counts only, exactly as admin_credential_state: a signed license envelope is a
    # credential and never leaves the function. Whether the variable is there at all, whether it is
    # a Secrets Manager reference the server resolves at startup, and whether any trusted key was
    # supplied to verify the signature are what separate "this deployment is Community because it
    # was never given a license" from "it was given one the server would not accept".
    try:
        variables = config(function)["Configuration"]["Environment"]["Variables"]
    except (RuntimeError, KeyError, ValueError, OSError):
        return "unreadable", "unknown", "unknown"
    keys = str(sum(1 for name in variables if name.startswith(LICENSE_TRUSTED_KEY_PREFIX)))
    value = variables.get(LICENSE_CONTENT_VARIABLE, "")
    if not value.strip():
        return "absent", "none", keys
    return "present", ("secretsmanager-reference"
                       if value.lower().startswith(SECRET_REFERENCE_PREFIX) else "inline"), keys


def license_status(function):
    # The server's own account of the envelope it was handed. LicenseOperationMiddleware lets
    # /api/v1/admin/license through even when the deployment license itself is blocked, so this
    # answers on exactly the deployment that just refused the write. Names and fixed enum values
    # only: never the licensee, the license id, or the envelope.
    try:
        status, body, _, _ = invoke(function, LICENSE_STATUS)
        if status != 200 or not isinstance(body, dict):
            return "unreadable", "unknown", "unknown"
        data = body.get("data") if isinstance(body.get("data"), dict) else {}
        entitled = "unknown"
        if isinstance(data.get("entitlements"), list):
            entitled = str(any(
                isinstance(item, dict) and item.get("isActive") is True
                and str(item.get("key", "")).lower() == FEATURESERVER_EDITS_ENTITLEMENT
                for item in data["entitlements"])).lower()
        return (redacted(data.get("edition", ""), 20) or "unknown",
                redacted(data.get("validationState", ""), 40) or "unknown",
                entitled)
    except Exception:  # noqa: BLE001 - a diagnostic must never replace the assertion it explains
        return "unreadable", "unknown", "unknown"


def refused_entitlement(path):
    return (FEATURESERVER_EDITS_ENTITLEMENT
            if "/FeatureServer/" in path and path.endswith(FEATURESERVER_EDIT_OPERATIONS)
            else "none")


def report_payment_required(function, path):
    # Run 28 (34320738962) reached the run-owned write with everything before it green and stopped
    # on `error=402` alone. A GeoServices refusal is HTTP 200 with the failure only in the body, so
    # the status said nothing, and the two deployments that produce this code - one carrying no
    # license at all, one carrying an envelope the server rejected - are opposite owners. Say which,
    # from the function's own configuration and the server's own verdict, in the run that failed.
    presence, source, trusted = license_configuration_state(function)
    edition, validation, entitled = license_status(function)
    # `entitlement=none` is the whole-deployment refusal: the license itself is unusable and the
    # server is refusing every gated surface, not this one operation. Reading it as an edit
    # entitlement would send that to the wrong owner, so the path decides which of the two it is.
    print(f"serving-402: phase={_phase} entitlement={refused_entitlement(path)} "
          f"variable={LICENSE_CONTENT_VARIABLE} presence={presence} source={source} "
          f"trusted-keys={trusted} edition={edition} validation={validation} "
          f"entitled={entitled}", file=sys.stderr)


# Lambda answers an initialization failure, a handler exception and a timeout the same way at the
# API: HTTP 200 with FunctionError set, the reason only in the invocation's response payload, and
# the platform's own account of it only in the log tail. Live run 25 (34305710517) stopped on that
# bare "Lambda invocation failed" and the job log said nothing else, so the run could not name the
# side that had failed - the per-run candidate or the standing alias - nor whether the function had
# died initializing, thrown while serving, or run out of time. Every one of those has a different
# owner, so say which, from the invoke's own answer.
# Only these establish that the runtime never reached the handler. Runtime.ExitError and
# Runtime.ExitCode are deliberately NOT here: Lambda raises them whenever the runtime process dies,
# which happens during an invocation as readily as during initialization, so reading them as "init"
# would send a serving crash to the wrong owner. They get their own answer below.
INIT_ERROR_TYPE = re.compile(r"^Init", re.IGNORECASE)
RUNTIME_EXIT = re.compile(r"^Runtime[.]", re.IGNORECASE)
TIMED_OUT = re.compile(r"task timed out", re.IGNORECASE)
# The platform's own verdict on the initialization that ran in this environment.
FAILED_INIT_REPORT = re.compile(r"^INIT_REPORT\b.*Status: (?:error|timeout)", re.MULTILINE)
# Platform- and runtime-authored lines only. The tail also carries the application's own stdout,
# and the lane cannot redact what it never held: HONUA_ADMIN_PASSWORD reaches the function as an
# `aws:secretsmanager:` reference, so the password the server resolves per request is a value no
# redaction set here can contain. These lines are written by Lambda itself and carry durations,
# request ids and error types - never configuration or resolved secrets.
PLATFORM_LOG_LINE = re.compile(
    r"^(?:START|END|REPORT|INIT_REPORT|RESTORE_REPORT|EXTENSION|Runtime[.]|RequestId:\s+\S+\s+Error:)")
INVOKE_LOG_TAIL_LINES = 20
# serve() shifts the standing alias to the newly published candidate version before the candidate
# phase, so "qualified" and "standing" stop being the same thing for the rest of the run.
_published_candidate = [""]
# Phases whose invocations run the candidate artifact whatever they are addressed through.
CANDIDATE_PHASES = ("deployed", "denied-key-mint", "candidate")


def invoke_target(function, meta=None):
    # Which side of the certification the invocation landed on, never which function: the standing
    # function is a fingerprint everywhere else in this evidence. An unqualified name is the per-run
    # function. A qualified one is the alias, which serves the candidate's own published version for
    # part of the run - so the version Lambda says it executed decides it, and the phase answers
    # when the invoke never reached a version (a dry-run status carries none).
    if ":" not in function:
        return "candidate"
    executed = str((meta or {}).get("ExecutedVersion") or "")
    if executed and _published_candidate[0]:
        return "candidate" if executed == _published_candidate[0] else "standing-alias"
    return "candidate" if _phase in CANDIDATE_PHASES else "standing-alias"


def invoke_log_tail(meta):
    # --log-type Tail already carries this invocation's own log back with the response, so the
    # initialization that failed is in hand without a CloudWatch query, a delivery wait, or a
    # permission on another function's log group.
    try:
        return base64.b64decode(meta.get("LogResult") or "").decode(errors="replace")
    except (ValueError, TypeError):
        return ""


def invoke_failure_kind(payload, tail):
    # The platform's own verdict first: an INIT_REPORT that ended in error or timeout is an
    # initialization failure whatever the payload says.
    if FAILED_INIT_REPORT.search(tail):
        return "init"
    error_type = str(payload.get("errorType", ""))
    if INIT_ERROR_TYPE.match(error_type):
        return "init"
    if TIMED_OUT.search(str(payload.get("errorMessage", ""))):
        return "timeout"
    if RUNTIME_EXIT.match(error_type):
        # The runtime process died and the tail did not say in which phase. Say that, rather than
        # picking one: an operator reading "init" would go looking at startup for a handler crash.
        return "runtime-exit"
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
    # The platform's own account of this exact request, and the only place a failed initialization
    # is reported. Restricted to lines Lambda itself wrote (see PLATFORM_LOG_LINE) and still passed
    # through the same redaction as every other echoed diagnostic, line by line and bounded.
    platform = [entry for entry in tail.splitlines() if PLATFORM_LOG_LINE.match(entry.strip())]
    for line in platform[-INVOKE_LOG_TAIL_LINES:]:
        print(f"serving-invoke-log: {redacted(line.strip(), 200)}", file=sys.stderr)


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
            report_invoke_failure(invoke_target(function, meta), path, meta, response)
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
        code, message, details, body_code = "", "", "", None
        if isinstance(body, dict):
            err = body.get("error")
            if isinstance(err, dict):
                body_code = err.get("code")
                code = redacted(body_code if body_code is not None else "", 40)
                # A GeoServices operation reports a refusal as HTTP 200 with the whole reason in the
                # envelope, so neither the status nor the code separates an invalid geometry from an
                # unknown layer, a read-only layer, a missing required field or a gated surface. Run
                # 28 (34320738962) failed the run-owned write on `error=402` and nothing else, and
                # naming the cause took the server sources. The message and the server's own details
                # are where it is written; both are echoed under the same redaction as every other
                # diagnostic, and bounded.
                message = redacted(err.get("message", ""), 200)
                details = redacted(join_details(err.get("details")), 200)
            code = code or redacted(body.get("title", body.get("type", "")), 60)
        print(f"serving-assertion: phase={_phase} path={path} status={status} expected={expect} "
              f"body-kind={body_kind(body)} error={code or 'none'} message={message or 'none'} "
              f"details={details or 'none'}", file=sys.stderr)
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
        if status == PAYMENT_REQUIRED or body_code == PAYMENT_REQUIRED:
            report_payment_required(function, path)
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
    load_cloned_secrets(directory)
    function, alias = inputs()
    # Separate CLI process: resolve again from the exact configuration cloned during preparation.
    admin_key(json.loads((directory / "standing.json").read_text()))
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
        # From here the standing alias serves the candidate for part of the run, so a failure on it
        # is attributed by the version Lambda executed rather than by the qualifier.
        _published_candidate[0] = str(candidate)
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
            load_cloned_secrets(sys.argv[2])
            report_invoke_failure(sys.argv[3], sys.argv[4], json.loads(Path(sys.argv[5]).read_text() or "{}"),
                                  sys.argv[6])
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
