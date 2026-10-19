#!/usr/bin/env python3
"""Protected-runner entrypoint for a standing AWS cert plan; never publishes private bytes."""
import hashlib
import json
import os
import re
import stat
import subprocess
import sys
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone
from pathlib import Path

SHA = r"[0-9a-f]{64}"
REV = r"[0-9a-f]{40}"
PUBLIC_KEYS = {"schema", "status", "approvalStatus", "qualificationStatus", "iacRevision",
               "candidateDigest", "planSha256", "planMetadataDigest", "expiresAtUtc", "packetSha256"}


def private_path(path, *, exists=True):
    path = Path(path)
    if not path.is_absolute() or ".." in path.parts:
        raise ValueError("private path")
    for parent in path.parents:
        info = parent.lstat()
        if stat.S_ISLNK(info.st_mode) or info.st_uid not in (0, os.getuid()):
            raise ValueError("private parent")
        if info.st_mode & 0o022 and not (info.st_uid == 0 and info.st_mode & stat.S_ISVTX):
            raise ValueError("private parent")
    if exists:
        info = path.lstat()
        if not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid() or stat.S_IMODE(info.st_mode) != 0o600:
            raise ValueError("private file")
    elif path.exists() or path.is_symlink():
        raise ValueError("token destination occupied")
    return path


def oidc_token():
    url = os.environ["ACTIONS_ID_TOKEN_REQUEST_URL"]
    auth = os.environ["ACTIONS_ID_TOKEN_REQUEST_TOKEN"]
    parsed = urllib.parse.urlsplit(url)
    # GitHub documents *.actions.githubusercontent.com for token retrieval;
    # runners may use a regional, sharded host instead of the base pipelines host.
    if (parsed.scheme != "https" or not parsed.hostname
            or not parsed.hostname.endswith(".actions.githubusercontent.com")
            or parsed.username or parsed.password or parsed.port not in (None, 443)):
        raise ValueError("OIDC issuer endpoint")
    query = urllib.parse.parse_qsl(parsed.query, keep_blank_values=True)
    query.append(("audience", "sts.amazonaws.com"))
    target = urllib.parse.urlunsplit((parsed.scheme, parsed.netloc, parsed.path,
                                      urllib.parse.urlencode(query), ""))
    request = urllib.request.Request(target, headers={"Authorization": "Bearer " + auth})
    class NoRedirect(urllib.request.HTTPRedirectHandler):
        def redirect_request(self, request, fp, code, msg, headers, newurl):
            return None

    with urllib.request.build_opener(NoRedirect).open(request, timeout=20) as response:
        result = json.load(response)
    token = result["value"]
    if not isinstance(token, str) or token.count(".") != 2:
        raise ValueError("OIDC response")
    return token


def main():
    os.umask(0o077)
    if os.environ.get("GITHUB_REPOSITORY") != "honua-io/honua-server" or os.environ.get("GITHUB_REF") != "refs/heads/trunk":
        raise ValueError("workflow identity")
    if any(k.upper() in ("HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "AWS_CA_BUNDLE",
                         "REQUESTS_CA_BUNDLE", "SSL_CERT_FILE", "CURL_CA_BUNDLE",
                         "LD_PRELOAD", "LD_LIBRARY_PATH", "BASH_ENV", "ENV")
           or k.startswith(("GIT_", "PYTHON")) for k in os.environ):
        raise ValueError("environment override")
    revision = os.environ["CERT_IAC_REVISION"]
    request_sha = os.environ["CERT_REQUEST_SHA256"]
    candidate = os.environ["CERT_CANDIDATE_SHA256"]
    if not (re.fullmatch(REV, revision) and re.fullmatch(SHA, request_sha)
            and re.fullmatch(SHA, candidate)):
        raise ValueError("dispatch pins")
    source = Path(os.environ["CERT_IAC_SOURCE"])
    if not source.is_absolute() or ".." in source.parts or any(node.is_symlink() for node in (source, *source.parents)):
        raise ValueError("source path")
    request_file = private_path(os.environ["CERT_REQUEST_FILE"])
    raw = request_file.read_bytes()
    if hashlib.sha256(raw).hexdigest() != request_sha:
        raise ValueError("request digest")
    approved = json.loads(raw)
    if approved["iacRevision"] != revision or approved["candidateDigest"] != candidate:
        raise ValueError("request pins")
    expiry = datetime.fromisoformat(approved["sessionExpiresAtUtc"].replace("Z", "+00:00"))
    if not (datetime.now(timezone.utc) + timedelta(minutes=5) < expiry
            <= datetime.now(timezone.utc) + timedelta(minutes=55)):
        raise ValueError("session bound")
    prepared_token = os.environ.get("CERT_BACKEND_TOKEN_PREPARED") == "1"
    token_path = private_path(approved["backendTokenFile"], exists=prepared_token)
    git_env = dict(os.environ, GIT_NO_REPLACE_OBJECTS="1", GIT_CONFIG_COUNT="2",
                   GIT_CONFIG_KEY_0="core.fsmonitor", GIT_CONFIG_VALUE_0="false",
                   GIT_CONFIG_KEY_1="core.untrackedCache", GIT_CONFIG_VALUE_1="false")
    if subprocess.check_output(["git", "-C", str(source), "rev-parse", "HEAD"],
                               stderr=subprocess.DEVNULL, env=git_env).decode().strip() != revision:
        raise ValueError("IaC revision")
    if subprocess.check_output(["git", "-C", str(source), "status", "--porcelain",
                                "--untracked-files=all", "--ignore-submodules=none"],
                               stderr=subprocess.DEVNULL, env=git_env).strip():
        raise ValueError("IaC source drift")
    flags = subprocess.check_output(["git", "-C", str(source), "ls-files", "-v", "-z"],
                                    stderr=subprocess.DEVNULL, env=git_env)
    if any(entry[:2] != b"H " for entry in flags.split(b"\0") if entry):
        raise ValueError("IaC hidden index")
    script_rel = "infrastructure/terraform/validation/scripts/aws/standing_cert_plan.py"
    script = source / script_rel
    if script.is_symlink() or not script.is_file() or script.read_bytes() != subprocess.check_output(
            ["git", "-C", str(source), "show", "HEAD:" + script_rel], stderr=subprocess.DEVNULL,
            env=git_env):
        raise ValueError("IaC producer source")
    try:
        if not prepared_token:
            token = oidc_token()
            fd = os.open(token_path, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600)
            with os.fdopen(fd, "w") as stream:
                stream.write(token)
                stream.flush()
                os.fsync(stream.fileno())
        child_env = {key: value for key, value in os.environ.items()
                     if not key.startswith("ACTIONS_") and key not in ("GITHUB_TOKEN", "GH_TOKEN")}
        result = subprocess.run(["python3", "-I", str(script), "--request", str(request_file)], cwd=source,
                                env=child_env, capture_output=True, text=True, timeout=2100, check=False)
        document = json.loads(result.stdout)
        expiry = datetime.fromisoformat(document["expiresAtUtc"])
        if (result.returncode != 0 or set(document) != PUBLIC_KEYS
                or document["schema"] != "honua.aws-cert-plan-public/v1"
                or document["status"] != "planned-unapproved"
                or document["approvalStatus"] != "not-approved"
                or document["qualificationStatus"] != "not-evaluated"
                or document["iacRevision"] != revision or document["candidateDigest"] != candidate
                or not all(isinstance(document[key], str) and re.fullmatch(SHA, document[key])
                           for key in ("planSha256", "planMetadataDigest", "packetSha256"))
                or expiry.tzinfo is None or not (datetime.now(timezone.utc) < expiry
                                                 <= datetime.now(timezone.utc) + timedelta(minutes=31))):
            raise ValueError("plan refused")
        print(json.dumps(document, sort_keys=True))
    finally:
        token_path.unlink(missing_ok=True)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        # The private producer keeps its diagnostics on the custodian-owned disk.
        print('{"schema":"honua.aws-cert-plan-public/v1","status":"refused","reason":"private-plan-workflow-refused"}')
        sys.exit(3)
