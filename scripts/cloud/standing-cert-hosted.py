#!/usr/bin/env python3
"""Ephemeral, fail-closed setup and sealing for the protected cert plan job."""
import base64
import hashlib
import io
import json
import os
import re
import shutil
import stat
import subprocess
import sys
import urllib.parse
import urllib.request
import zipfile
from datetime import datetime, timedelta, timezone
from pathlib import Path

SHA = r"[0-9a-f]{64}"
REV = r"[0-9a-f]{40}"
ROOT_REL = "infrastructure/terraform/examples/aws-cert"
SCRIPT_REL = "infrastructure/terraform/validation/scripts/aws"
STAGE = Path.home() / ".local/state/honua/cert-plan-stage"
EXPORT = Path(os.environ.get("GITHUB_WORKSPACE", "/invalid")) / "sealed"
TERRAFORM_URL = "https://releases.hashicorp.com/terraform/1.13.5/terraform_1.13.5_linux_amd64.zip"
TERRAFORM_ARCHIVE_SHA = "0dbe3fcc268eb670801af6a6456799d1ae26e72e73797f6c6167e18aafd1fd9a"


def require(value, reason):
    if not value:
        raise ValueError(reason)


def reject_runtime_overrides():
    """Refuse inherited routing/startup controls before Terraform or AWS calls."""
    prefixes = ("TF_CLI_ARGS", "TF_VAR_", "TF_LOG", "GIT_", "HONUA_IAC_", "AWS_ENDPOINT_URL",
                "BASH_ENV", "BASH_FUNC_", "BASHOPTS", "SHELLOPTS", "LD_", "PYTHON", "ENV")
    exact = {"TF_DATA_DIR", "TF_WORKSPACE", "TF_CLI_CONFIG_FILE", "TERRAFORM_CONFIG",
             "TF_REATTACH_PROVIDERS", "AWS_CA_BUNDLE", "AWS_PROFILE", "AWS_DEFAULT_PROFILE",
             "AWS_CONFIG_FILE", "AWS_SHARED_CREDENTIALS_FILE", "AWS_WEB_IDENTITY_TOKEN_FILE",
             "AWS_ROLE_ARN", "REQUESTS_CA_BUNDLE", "SSL_CERT_FILE", "CURL_CA_BUNDLE",
             "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"}
    require(not any(key.startswith(prefixes) or key.upper() in exact for key in os.environ),
            "ambient-override-refused")


def sha(data):
    return hashlib.sha256(data).hexdigest()


def save(path, data):
    with path.open("xb") as output:
        output.write(data)
        output.flush()
        os.fsync(output.fileno())
    path.chmod(0o600)


def extract_terraform_archive(data, directory, expected_sha=TERRAFORM_ARCHIVE_SHA):
    require(len(data) <= 64 * 1024 * 1024 and sha(data) == expected_sha,
            "terraform-archive-mismatch")
    with zipfile.ZipFile(io.BytesIO(data)) as archive:
        require(archive.namelist() == ["LICENSE.txt", "terraform"],
                "terraform-archive-members-invalid")
        member = archive.getinfo("terraform")
        require(member.file_size <= 128 * 1024 * 1024
                and stat.S_IFMT(member.external_attr >> 16) in (0, stat.S_IFREG),
                "terraform-member-invalid")
        content = archive.read(member)
    require(len(content) == member.file_size, "terraform-member-invalid")
    directory.mkdir(mode=0o700)
    target = directory / "terraform"
    save(target, content)
    target.chmod(0o700)
    return target


def install_terraform():
    with urllib.request.urlopen(TERRAFORM_URL, timeout=60) as response:
        data = response.read(64 * 1024 * 1024 + 1)
    directory = Path(os.environ["RUNNER_TEMP"]) / "cert-plan-bin"
    extract_terraform_archive(data, directory)
    with open(os.environ["GITHUB_PATH"], "a") as path:
        path.write(str(directory) + "\n")
    print('{"status":"terraform-verified","version":"1.13.5"}')


def source():
    location = Path(os.environ["CERT_IAC_SOURCE"])
    require(location.is_absolute() and not location.is_symlink(), "source-path-invalid")
    expected = os.environ["CERT_IAC_REVISION"]
    require(re.fullmatch(REV, expected), "source-revision-invalid")
    git = ["git", "-c", "core.fsmonitor=false", "-c", "core.untrackedCache=false",
           "-C", str(location)]
    git_env = {"PATH": os.environ["PATH"], "HOME": os.environ["HOME"], "LANG": "C.UTF-8",
               "GIT_NO_REPLACE_OBJECTS": "1"}
    actual = subprocess.check_output([*git, "rev-parse", "HEAD"],
                                     stderr=subprocess.DEVNULL, env=git_env).decode().strip()
    require(actual == expected, "source-revision-mismatch")
    require(not subprocess.check_output([*git, "status", "--porcelain",
                                         "--untracked-files=normal"], stderr=subprocess.DEVNULL, env=git_env),
            "source-dirty")
    flags = subprocess.check_output([*git, "ls-files", "-v", "-z"],
                                    stderr=subprocess.DEVNULL, env=git_env)
    require(all(entry[:2] == b"H " for entry in flags.split(b"\0") if entry),
            "source-hidden-index")
    for relative in ("infrastructure/terraform/validation/scripts/aws/standing_cert_plan.py",
                     "infrastructure/terraform/validation/scripts/aws/cert_plan_envelope.py",
                     "infrastructure/terraform/validation/scripts/aws/verify_reviewed_modules.py",
                     "infrastructure/terraform/validation/scripts/aws/restore_reviewed_bootstrap.py",
                     "release/aws-cert-plan-recipient.json", "release/aws-cert-reviewed-modules.json",
                     "release/aws-cert-reviewed-bootstrap.json"):
        path = location / relative
        require(path.is_file() and not path.is_symlink() and path.read_bytes() ==
                subprocess.check_output([*git, "show", "HEAD:" + relative],
                                        stderr=subprocess.DEVNULL, env=git_env),
                "source-input-mismatch")
    return location


def run_private(args, *, cwd=None, env=None, timeout=600):
    with (STAGE / "setup.log").open("ab") as log:
        result = subprocess.run(args, cwd=cwd, env=env, stdout=subprocess.PIPE,
                                stderr=log, timeout=timeout, check=False)
        require(result.returncode == 0, "private-setup-step-refused")
        return result.stdout


def fixed_request():
    request = json.loads((STAGE / "request.json").read_bytes())
    lifetime = request["expiresInSeconds"]
    expiry = datetime.fromisoformat(request["sessionExpiresAtUtc"].replace("Z", "+00:00"))
    now = datetime.now(timezone.utc)
    account = request["accountId"]
    deployment = request["executionIdentityContract"]["roles"]["infra_deployment"]["role_arn"]
    backend_role = request["workloadIdentityContract"]["role_arn"]
    require(request["schema"] == "honua.aws-cert-plan-request/v2"
            and request["sealedStaging"]["mode"] == "age-v1.3.2-exact-run"
            and request["privateStaging"]["directory"] == str(STAGE / "private")
            and request["varFile"] == str(STAGE / "terraform.tfvars.json")
            and request["backendTokenFile"] == str(STAGE / "backend-token")
            and request["iacRevision"] == os.environ["CERT_IAC_REVISION"]
            and request["candidateDigest"] == os.environ["CERT_CANDIDATE_SHA256"]
            and re.fullmatch(SHA, request["backendSourceSha256"])
            and type(lifetime) is int and 60 <= lifetime <= 1800
            and expiry.tzinfo is not None
            and now + timedelta(seconds=max(300, lifetime + 120)) < expiry
            <= now + timedelta(minutes=55)
            and re.fullmatch(r"[0-9]{12}", account)
            and request["region"] == os.environ["AWS_REGION"]
            and deployment == os.environ["CERT_DEPLOY_ROLE_ARN"]
            and backend_role != deployment
            and all(re.fullmatch(rf"arn:aws:iam::{account}:role/[A-Za-z0-9+=,.@_/-]+", role)
                    for role in (deployment, backend_role)),
            "hosted-request-invalid")
    return request


def canonical_backend_source(request):
    """Only the reviewed existing S3 backend shape may run before producer checks."""
    config = request["backend"]
    role = request["workloadIdentityContract"]["role_arn"]
    token = request["backendTokenFile"]
    require(all(isinstance(config[key], str) and config[key] for key in ("bucket_id", "object_key")),
            "backend-request-invalid")
    lines = ["terraform {", '  backend "s3" {',
             "    bucket = " + json.dumps(config["bucket_id"]),
             "    key = " + json.dumps(config["object_key"]),
             "    region = " + json.dumps(request["region"]),
             "    encrypt = true", "    use_lockfile = true",
             "    assume_role_with_web_identity = {",
             "      role_arn = " + json.dumps(role),
             "      web_identity_token_file = " + json.dumps(token),
             '      duration = "1h"', "    }", "  }", "}"]
    return ("\n".join(lines) + "\n").encode()


def admit_backend_source(request, data):
    require(sha(data) == request["backendSourceSha256"]
            and data == canonical_backend_source(request), "backend-source-refused")


def normalize_reviewed_module_cache(root):
    """Narrow archive modes inside the private Terraform data tree before admission."""
    data = root / ".terraform"
    info = data.lstat()
    require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid()
            and stat.S_IMODE(info.st_mode) == 0o700, "module-staging-not-private")
    cache = data / "modules"
    info = cache.lstat()
    require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid(), "module-cache-invalid")
    cache.chmod(0o700)
    index_path = cache / "modules.json"
    info = index_path.lstat()
    require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid(), "module-index-invalid")
    index_path.chmod(0o600)
    index = json.loads(index_path.read_bytes())
    sources = {"registry.terraform.io/terraform-aws-modules/vpc/aws",
               "registry.terraform.io/terraform-aws-modules/rds/aws"}
    packages = []
    for item in index["Modules"]:
        if item.get("Source") not in sources:
            continue
        location = Path(item.get("Dir", ""))
        require(not location.is_absolute() and len(location.parts) == 3
                and location.parts[:2] == (".terraform", "modules"),
                "module-package-location-invalid")
        packages.append((item["Source"], cache / location.parts[2]))
    require({source for source, _ in packages} == sources and len(packages) == 2
            and len({path for _, path in packages}) == 2, "module-package-index-invalid")
    for _, package in packages:
        count, total = 0, 0
        for parent, children, files in os.walk(package, followlinks=False):
            directory = Path(parent)
            info = directory.lstat()
            require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid(),
                    "module-package-entry-invalid")
            directory.chmod(0o700)
            for name in children:
                path = directory / name
                info = path.lstat()
                require(stat.S_ISDIR(info.st_mode) and info.st_uid == os.getuid(),
                        "module-package-entry-invalid")
                path.chmod(0o700)
            for name in files:
                path = directory / name
                info = path.lstat()
                count += 1
                total += info.st_size
                require(stat.S_ISREG(info.st_mode) and info.st_uid == os.getuid()
                        and count <= 512 and total <= 32 * 1024 * 1024,
                        "module-package-entry-invalid")
                path.chmod(0o644)
        require(count > 0, "module-package-empty")
        for parent, children, _ in os.walk(package, followlinks=False):
            for name in children:
                (Path(parent) / name).chmod(0o755)
        package.chmod(0o755)


def prepare():
    reject_runtime_overrides()
    require(os.environ.get("GITHUB_REPOSITORY") == "honua-io/honua-server"
            and os.environ.get("GITHUB_REF") == "refs/heads/trunk"
            and os.environ.get("RUNNER_ENVIRONMENT") == "github-hosted", "workflow-context-invalid")
    require(re.fullmatch(SHA, os.environ["CERT_REQUEST_SHA256"])
            and re.fullmatch(SHA, os.environ["CERT_CANDIDATE_SHA256"]), "dispatch-digest-invalid")
    iac = source()
    require(not STAGE.exists() and not STAGE.is_symlink() and not EXPORT.exists(), "staging-occupied")
    STAGE.parent.mkdir(mode=0o700, parents=True, exist_ok=True)
    STAGE.mkdir(mode=0o700)
    (STAGE / "private").mkdir(mode=0o700)
    (STAGE / "tools").mkdir(mode=0o700)
    save(STAGE / "setup.log", b"")
    request_bytes = os.environ["CERT_REQUEST_JSON"].encode()
    require(sha(request_bytes) == os.environ["CERT_REQUEST_SHA256"], "request-digest-mismatch")
    save(STAGE / "request.json", request_bytes)
    request = fixed_request()
    root = iac / ROOT_REL
    backend_file = root / "backend.tf"
    require(not backend_file.exists() and not backend_file.is_symlink(), "backend-file-occupied")
    backend_bytes = os.environ["CERT_BACKEND_HCL"].encode()
    admit_backend_source(request, backend_bytes)
    save(backend_file, backend_bytes)
    vars_bytes = os.environ["CERT_TFVARS_JSON"].encode()
    require(sha(vars_bytes) == request["varFileSha256"], "tfvars-digest-mismatch")
    save(STAGE / "terraform.tfvars.json", vars_bytes)
    save(STAGE / "terraform.tfrc", b"")
    setup_env = {key: value for key, value in os.environ.items()
                 if key not in ("CERT_REQUEST_JSON", "CERT_TFVARS_JSON", "CERT_BACKEND_HCL")}
    envelope = iac / SCRIPT_REL / "cert_plan_envelope.py"
    preflight = json.loads(run_private(["python3", "-I", str(envelope), "preflight-recipient"],
                                       env=setup_env))
    require(preflight["status"] == "recipient-verified"
            and preflight["iacRevision"] == request["iacRevision"]
            and preflight["recipientSha256"] == request["sealedStaging"]["recipientSha256"],
            "recipient-preflight-invalid")
    run_private(["python3", "-I", str(envelope), "install-tools", "--directory", str(STAGE / "tools")],
                env=setup_env, timeout=120)
    terraform = shutil.which("terraform")
    require(terraform is not None, "pinned-terraform-required")
    version = json.loads(run_private([terraform, "version", "-json"], env=setup_env))["terraform_version"]
    require(version == request["terraformVersion"] == "1.13.5", "terraform-version-mismatch")
    init_env = dict(setup_env, TF_CLI_CONFIG_FILE=str(STAGE / "terraform.tfrc"),
                    TF_IN_AUTOMATION="1", CHECKPOINT_DISABLE="1", AWS_EC2_METADATA_DISABLED="true")
    for key in list(init_env):
        if key.startswith("AWS_") or key.startswith("TF_CLI_ARGS") or key.startswith("GIT_"):
            init_env.pop(key)
    data = root / ".terraform"
    require(not data.exists() and not data.is_symlink(), "module-staging-occupied")
    data.mkdir(mode=0o700)
    run_private([terraform, "-chdir=" + str(root), "init", "-backend=false", "-lockfile=readonly",
                 "-input=false", "-no-color"], env=init_env, timeout=600)
    normalize_reviewed_module_cache(root)
    verified = json.loads(run_private(["python3", "-I", str(iac / SCRIPT_REL / "verify_reviewed_modules.py"),
                                       "--root", str(root), "--manifest",
                                       str(iac / "release/aws-cert-reviewed-modules.json")], env=setup_env))
    require(verified["status"] == "verified", "module-preflight-invalid")
    require(sha((root / ".terraform.lock.hcl").read_bytes()) == request["providerLockSha256"],
            "provider-lock-mismatch")
    print(json.dumps({"status": "prepared", "iacRevision": request["iacRevision"],
                      "moduleManifestSha256": verified["manifestSha256"]}, sort_keys=True))


def backend():
    reject_runtime_overrides()
    iac = source()
    request = fixed_request()
    root = iac / ROOT_REL
    backend_file = root / "backend.tf"
    require(backend_file.is_file() and not backend_file.is_symlink()
            and stat.S_IMODE(backend_file.stat().st_mode) == 0o600,
            "backend-source-missing")
    admit_backend_source(request, backend_file.read_bytes())
    # Existing OIDC trust is for the protected cert environment. A separate
    # backend role consumes this short-lived token; no broadening of trust.
    import importlib.util
    spec = importlib.util.spec_from_file_location("standing_cert_plan_only", Path(__file__).with_name("standing-cert-plan-only.py"))
    entry = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(entry)
    token = entry.oidc_token()
    save(STAGE / "backend-token", token.encode())
    terraform = shutil.which("terraform")
    require(terraform is not None, "pinned-terraform-required")
    env = dict(os.environ, TF_CLI_CONFIG_FILE=str(STAGE / "terraform.tfrc"),
               TF_IN_AUTOMATION="1", CHECKPOINT_DISABLE="1")
    run_private([terraform, "-chdir=" + str(root), "init", "-reconfigure", "-lockfile=readonly",
                 "-input=false", "-no-color"], env=env, timeout=600)
    workspace = request["backend"]["workspace"]
    require(re.fullmatch(r"[A-Za-z0-9_-]{1,64}", workspace), "workspace-invalid")
    run_private([terraform, "-chdir=" + str(root), "workspace", "select", workspace], env=env)
    state = json.loads(run_private([terraform, "-chdir=" + str(root), "state", "pull"], env=env))
    require(state.get("lineage") == request["stateBefore"]["lineage"]
            and state.get("serial") == request["stateBefore"]["serial"], "existing-state-mismatch")
    bootstrap = json.loads((iac / "release/aws-cert-reviewed-bootstrap.json").read_bytes())
    function = request["bootstrapFunctionArn"]
    require(re.fullmatch(r"arn:aws:lambda:[a-z0-9-]+:[0-9]{12}:function:[A-Za-z0-9_-]+", function),
            "bootstrap-function-invalid")
    record = json.loads(run_private(["aws", "lambda", "get-function", "--function-name", function,
                                     "--region", request["region"], "--output", "json"], env=env))
    require(record["Configuration"]["FunctionArn"] == function
            and base64.b64decode(record["Configuration"]["CodeSha256"]).hex() == bootstrap["zipSha256"],
            "bootstrap-deployed-hash-mismatch")
    url = urllib.parse.urlsplit(record["Code"]["Location"])
    require(url.scheme == "https" and url.hostname and url.hostname.endswith(".amazonaws.com")
            and url.username is None and url.password is None and url.port in (None, 443)
            and not url.fragment, "bootstrap-download-location-invalid")
    with urllib.request.urlopen(record["Code"]["Location"], timeout=60) as response:
        content = response.read(64 * 1024 * 1024 + 1)
    require(len(content) == bootstrap["size"] and sha(content) == bootstrap["zipSha256"],
            "bootstrap-download-mismatch")
    save(STAGE / "deployed.zip", content)
    run_private(["python3", "-I", str(iac / SCRIPT_REL / "restore_reviewed_bootstrap.py"),
                 "--root", str(root), "--archive", str(STAGE / "deployed.zip"),
                 "--manifest", str(iac / "release/aws-cert-reviewed-bootstrap.json")])
    print('{"status":"existing-backend-and-bootstrap-verified"}')


def plan_and_seal():
    reject_runtime_overrides()
    iac = source()
    request = fixed_request()
    token_file = STAGE / "backend-token"
    require(token_file.is_file() and not token_file.is_symlink()
            and stat.S_IMODE(token_file.stat().st_mode) == 0o600, "backend-token-missing")
    import importlib.util
    spec = importlib.util.spec_from_file_location("standing_cert_plan_only", Path(__file__).with_name("standing-cert-plan-only.py"))
    entry = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(entry)
    renewed = STAGE / "backend-token-renewed"
    save(renewed, entry.oidc_token().encode())
    os.replace(renewed, token_file)
    env = dict(os.environ, CERT_REQUEST_FILE=str(STAGE / "request.json"),
               CERT_BACKEND_TOKEN_PREPARED="1")
    public = run_private(["python3", "-I", str(Path(__file__).with_name("standing-cert-plan-only.py"))],
                         env=env, timeout=2400)
    receipt = json.loads(public)
    require(receipt["status"] == "planned-unapproved", "producer-refused")
    save(STAGE / "public.json", public)
    packets = [p for p in (STAGE / "private").iterdir() if p.is_dir() and p.name.startswith("aws-cert-plan-")]
    require(len(packets) == 1 and (packets[0] / "packet.json").is_file(), "packet-ambiguous")
    envelope = iac / SCRIPT_REL / "cert_plan_envelope.py"
    sealed = json.loads(run_private(["python3", "-I", str(envelope), "seal", "--packet", str(packets[0]),
                                     "--producer-receipt", str(STAGE / "public.json"),
                                     "--output-directory", str(STAGE / "private"),
                                     "--tools-directory", str(STAGE / "tools")], timeout=180))
    exports = [p for p in (STAGE / "private").iterdir() if p.is_dir() and p.name.startswith("encrypted-plan-")]
    require(len(exports) == 1 and {p.name for p in exports[0].iterdir()} == {"plan.age", "receipt.json"},
            "export-ambiguous")
    validate_sealed_export(sealed, exports[0], receipt, request)
    EXPORT.mkdir(mode=0o700)
    for name in ("plan.age", "receipt.json"):
        shutil.copyfile(exports[0] / name, EXPORT / name)
    require(sha((EXPORT / "plan.age").read_bytes()) == sealed["ciphertextSha256"], "export-drift")
    print(json.dumps({"status": "sealed-unapproved", "ciphertextSha256": sealed["ciphertextSha256"]},
                     sort_keys=True))


def validate_sealed_export(sealed, directory, producer, request):
    """Admit only the canonical public receipt bound to this exact ciphertext."""
    fields = {"schema", "status", "context", "producer", "recipientSha256",
              "ciphertextSha256", "encryptionVersion", "approvalStatus", "qualificationStatus"}
    context = {"repository": "honua-io/honua-server",
               "workflow": ".github/workflows/standing-aws-cert-plan.yml",
               "ref": "refs/heads/trunk", "headSha": os.environ["GITHUB_SHA"],
               "runId": int(os.environ["GITHUB_RUN_ID"]),
               "attempt": int(os.environ["GITHUB_RUN_ATTEMPT"])}
    require(set(sealed) == fields and sealed["schema"] == "honua.aws-cert-plan-envelope/v1"
            and sealed["status"] == "encrypted-awaiting-local-retention"
            and sealed["context"] == context and sealed["producer"] == producer
            and sealed["recipientSha256"] == request["sealedStaging"]["recipientSha256"]
            and sealed["encryptionVersion"] == "age/v1.3.2"
            and sealed["approvalStatus"] == "not-approved"
            and sealed["qualificationStatus"] == "not-evaluated"
            and isinstance(sealed["ciphertextSha256"], str)
            and re.fullmatch(SHA, sealed["ciphertextSha256"]), "envelope-refused")
    ciphertext, receipt_file = directory / "plan.age", directory / "receipt.json"
    require(all(p.is_file() and not p.is_symlink() and stat.S_IMODE(p.stat().st_mode) == 0o600
                for p in (ciphertext, receipt_file))
            and ciphertext.stat().st_size <= 256 * 1024 * 1024
            and sha(ciphertext.read_bytes()) == sealed["ciphertextSha256"]
            and receipt_file.read_bytes() == json.dumps(sealed, sort_keys=True, indent=2).encode() + b"\n",
            "envelope-export-invalid")


if __name__ == "__main__":
    os.umask(0o077)
    try:
        operation = sys.argv[1]
        {"install-terraform": install_terraform, "prepare": prepare,
         "backend": backend, "plan-and-seal": plan_and_seal}[operation]()
    except Exception:
        print('{"status":"refused"}')
        sys.exit(3)
