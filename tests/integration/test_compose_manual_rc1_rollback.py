# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License.
"""Exercise the manual legacy configuration boundary against real images and data."""
import hashlib
import json
import os
from pathlib import Path
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.request

from test_compose_release_lifecycle import ROOT, free_port, run
from compose_quickstart import ALL_SECRET_KEYS, initialize, read_secret
from package_quickstart import package

RC1 = "ghcr.io/honua-io/honua-server@sha256:c8d7aef98761618145725530b6a4e4b25ea4a9ae121da168362f9be4cf5f7d37"


def retained_configuration():
    release = Path(os.environ["HONUA_DEPLOYMENT_CONFIGURATION_ROOT"]).resolve()
    selection = json.loads(run([sys.executable, str(release / "tools/deployment_configuration.py"),
                               "--root", str(release), "--image", RC1, "--kind", "compose"]))
    assert selection["rollbackMode"] == "manual"
    assert len(selection["files"]) == 1
    return selection["files"][0]


def wait_ready(port):
    deadline = time.monotonic() + 300
    while time.monotonic() < deadline:
        try:
            with urllib.request.urlopen(f"http://127.0.0.1:{port}/healthz/ready", timeout=5) as response:
                if response.status == 200:
                    return
        except (urllib.error.URLError, TimeoutError):
            pass
        time.sleep(2)
    raise AssertionError("manual deployment did not become ready")


def transfer_database(compose, env, dump=None):
    command = (["pg_dump", "-Fc"] if dump is None else
               ["pg_restore", "--clean", "--if-exists", "--exit-on-error"])
    result = subprocess.run(compose + ["exec", "-T", "postgres"] + command +
                            ["-U", "honua", "-d", "honua"], env=env, input=dump,
                            stdout=subprocess.PIPE, stderr=subprocess.PIPE, timeout=600)
    assert result.returncode == 0, "database backup or restore failed"
    return result.stdout


def assert_revision(compose, env, expected):
    container = run(compose + ["ps", "-q", "honua"], env=env)
    actual = run(["docker", "inspect", "--format", "{{.Image}}", container])
    assert actual == expected


def test_manual_catalog_rc1_rollback_and_return(tmp_path):
    candidate = os.environ["HONUA_LIFECYCLE_IMAGE"]
    executor = os.environ["HONUA_LIFECYCLE_EXECUTOR_IMAGE"]
    retained = retained_configuration()
    run(["docker", "pull", "--platform", "linux/amd64", RC1])
    prior_id = run(["docker", "image", "inspect", "--format", "{{.Id}}", RC1])
    install = tmp_path / "install"
    package(install, dict(server=candidate, executor=executor, worker=candidate), "2026.1-rc.2")
    port = free_port()
    settings = dict(COMPOSE_PROJECT_NAME="honua-manual-" + secrets.token_hex(4),
                    HONUA_HTTP_PORT=str(port), HONUA_GRPC_PORT=str(free_port()))
    (install / ".env").write_text("".join(f"{key}={value}\n" for key, value in settings.items()))
    env = {key: value for key, value in os.environ.items() if key not in ALL_SECRET_KEYS}
    env.update(initialize(install))
    common = ["docker", "compose", "--project-directory", str(install),
              "-p", env["COMPOSE_PROJECT_NAME"]]
    current = common + ["-f", str(install / "compose.yaml")]
    # rc.1 has no native readiness executable. Poll its HTTP endpoint from the host.
    override = install / "rc1-host-readiness.yaml"
    override.write_text("services:\n  honua:\n    healthcheck:\n      disable: true\n")
    legacy = common + ["-f", retained, "-f", str(override)]
    legacy_env = dict(env, HONUA_IMAGE=RC1)
    for key in ("POSTGRES_PASSWORD", "HONUA_ADMIN_PASSWORD", "HONUA_MASTER_KEY", "HONUA_KEYRING_PASSWORD"):
        legacy_env[key] = read_secret(install, key)
    def sql(statement):
        return run(current + ["exec", "-T", "postgres", "psql", "-U", "honua", "-d", "honua",
                              "-tA", "-v", "ON_ERROR_STOP=1", "-c", statement], env=env)
    try:
        run(current + ["up", "-d", "--wait", "postgres", "redis", "keyring-init"], env=env)
        run(legacy + ["up", "-d", "--no-deps", "honua"], env=legacy_env)
        wait_ready(port)
        assert_revision(legacy, legacy_env, prior_id)
        assert sql("SELECT max(substring(scriptname from '([0-9]+)_')::int) FROM public.schema_versions") == "140"
        sql("CREATE TABLE public.manual_rollback_marker(value text); INSERT INTO public.manual_rollback_marker VALUES ('retained')")
        run(legacy + ["stop", "honua"], env=legacy_env)
        dump = transfer_database(current, env)
        backup = install / "backups/manual-rc1.dump"
        backup.write_bytes(dump)
        backup.chmod(0o600)
        checksum = hashlib.sha256(dump).hexdigest()
        receipt = backup.with_suffix(".sha256")
        receipt.write_text(checksum + "\n")
        receipt.chmod(0o600)
        run(current + ["up", "-d", "--no-deps", "honua"], env=env)
        wait_ready(port)
        assert_revision(current, env, candidate)
        sql("UPDATE public.manual_rollback_marker SET value='changed-after-upgrade'")
        run(current + ["stop", "honua"], env=env)
        restored = backup.read_bytes()
        assert hashlib.sha256(restored).hexdigest() == receipt.read_text().strip()
        transfer_database(current, env, restored)
        run(legacy + ["up", "-d", "--no-deps", "honua"], env=legacy_env)
        wait_ready(port)
        assert_revision(legacy, legacy_env, prior_id)
        assert sql("SELECT value FROM public.manual_rollback_marker") == "retained"
        assert sql("SELECT max(substring(scriptname from '([0-9]+)_')::int) FROM public.schema_versions") == "140"
        run(legacy + ["stop", "honua"], env=legacy_env)
        run(current + ["up", "-d", "--no-deps", "honua"], env=env)
        wait_ready(port)
        assert_revision(current, env, candidate)
        assert sql("SELECT value FROM public.manual_rollback_marker") == "retained"
        rendered = run(current + ["config"], env=env) + run(current + ["config", "--environment"], env=env)
        assert not set(ALL_SECRET_KEYS) & set(env)
        assert all(read_secret(install, key) not in rendered for key in ALL_SECRET_KEYS)
        expected_schema = max(int(path.name.split("_", 1)[0]) for path in (ROOT / "src/Honua.Server/Migrations").glob("[0-9]*_*.sql"))
        assert int(sql("SELECT max(substring(scriptname from '([0-9]+)_')::int) FROM public.schema_versions")) == expected_schema
        assert sql("SELECT PostGIS_Version()")
    finally:
        run(current + ["down", "--volumes", "--remove-orphans"], env=env, check=False)
