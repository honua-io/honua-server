#!/usr/bin/env python3
"""Bounded real native-image migration-refusal regression (honua-server#5722)."""
from __future__ import annotations

import argparse
import json
import re
import subprocess
import time
import uuid
from pathlib import Path

REFUSAL = "Migration safety gate blocked pending contract-phase migration(s) on an existing database:"
CONTRACT = "038_DropV1MetadataGraphTables"
CONTAINER_LIMITS = ("--memory", "1g", "--memory-swap", "1g", "--cpus", "1", "--pids-limit", "256",
                    "--log-opt", "max-size=10m", "--log-opt", "max-file=1")
DUMP_LIMIT_BYTES = 64 * 1024 * 1024


SEED = """CREATE EXTENSION IF NOT EXISTS postgis;
CREATE TABLE public.schema_versions (
 schemaversionsid serial PRIMARY KEY, scriptname varchar(255) NOT NULL,
 applied timestamp without time zone NOT NULL);
INSERT INTO public.schema_versions (scriptname, applied)
VALUES ('Honua.Server.Migrations.002_CreateAttachmentsTable.sql', NOW());
"""


def docker(*args: str, timeout: int = 60, input: str | None = None) -> str:
    return subprocess.run(["docker", *args], check=True, capture_output=True,
                          text=True, timeout=timeout, input=input).stdout.strip()


def validate(exit_code: int, logs: str, dumps: list[str], oom: bool) -> None:
    if exit_code != 1 or oom:
        raise ValueError(f"expected clean exit 1, observed exit={exit_code}, OOMKilled={oom}")
    if REFUSAL not in logs or CONTRACT not in logs:
        raise ValueError("missing positive migration safety refusal and pending 038 contract evidence")
    if "Now listening on:" in logs or "Unhandled exception" in logs:
        raise ValueError("refused boot served requests or propagated an unhandled exception")
    if dumps:
        raise ValueError(f"crash dumps created: {dumps}")


def is_dump(path: str) -> bool:
    name = Path(path).name.lower()
    return name == "core" or name.startswith("core.") or name.endswith((".dmp", ".mdmp", ".core"))


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=240)
    args = parser.parse_args()
    if not re.fullmatch(r"[0-9a-f]{40}", args.source_sha) or not 1 <= args.timeout <= 600:
        parser.error("source SHA must be full lowercase SHA; timeout must be 1..600 seconds")
    args.output.mkdir(parents=True, exist_ok=True)
    suffix = uuid.uuid4().hex[:12]
    network, pg, server = (f"honua-refusal-{kind}-{suffix}" for kind in ("net", "pg", "server"))
    receipt: dict = {"schema": "honua.native-migration-refusal/v1", "source_sha": args.source_sha,
                     "requested_image": args.image, "passed": False,
                     "dump_file_limit_bytes": DUMP_LIMIT_BYTES,
                     "dump_scope": "container filesystem; host core collector not inspected; signal exit always fails",
                     "container_limits": list(CONTAINER_LIMITS),
                     "core_pattern": Path("/proc/sys/kernel/core_pattern").read_text().strip()}
    try:
        image = json.loads(docker("image", "inspect", args.image))[0]
        labels = image["Config"].get("Labels") or {}
        receipt.update(image_id=image["Id"], repo_digests=image.get("RepoDigests", []),
                       image_source_sha=labels.get("org.opencontainers.image.revision"))
        if labels.get("honua.runtime.compilation") != "native-aot":
            raise ValueError("image must declare native-aot compilation")
        if image["Config"].get("Entrypoint") != ["/app/Honua.Server"]:
            raise ValueError("expected production native server entrypoint")
        if receipt["image_source_sha"] != args.source_sha:
            raise ValueError("image revision does not match requested source SHA")
        # Never inherit approval/skip settings, including from a caller's environment.
        env = image["Config"].get("Env") or []
        forbidden = ("honua_skip_migrations", "honua_approve_contract_migrations", "database__migrationsafety__")
        if any(value.split("=", 1)[0].lower().startswith(forbidden) for value in env):
            raise ValueError("image bakes in migration skip, approval, or safety overrides")
        docker("network", "create", network)
        docker("run", "-d", "--name", pg, "--network", network, *CONTAINER_LIMITS,
               "-e", "POSTGRES_PASSWORD=refusal-test", "-e", "POSTGRES_DB=honua_refusal",
               "postgis/postgis:16-3.4")
        deadline = time.monotonic() + 120
        while True:
            try:
                docker("exec", pg, "pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "honua_refusal", timeout=10)
                break
            except subprocess.CalledProcessError:
                if time.monotonic() >= deadline:
                    raise TimeoutError("PostGIS did not become ready")
                time.sleep(2)
        docker("exec", "-i", pg, "psql", "-v", "ON_ERROR_STOP=1", "-U", "postgres",
               "-d", "honua_refusal", input=SEED)
        docker("create", "--name", server, "--network", network, *CONTAINER_LIMITS,
               "--ulimit", f"core={DUMP_LIMIT_BYTES}", "--ulimit", f"fsize={DUMP_LIMIT_BYTES}",
               "-e", "ASPNETCORE_ENVIRONMENT=Production", "-e", "DOTNET_ENVIRONMENT=Production",
               "-e", f"ConnectionStrings__DefaultConnection=Host={pg};Database=honua_refusal;Username=postgres;Password=refusal-test",
               "-e", "DataSource__Provider=postgis", "-e", "HONUA_ADMIN_PASSWORD=GatedBoot-Refusal-1a",
               "-e", "Security__ConnectionEncryption__MasterKey=gated-boot-refusal-connection-encryption-key",
               "-e", "Security__ConnectionEncryption__Salt=Z2F0ZWQtYm9vdC1yZWZ1c2FsLXNhbHQ=",
               "-e", "DOTNET_DbgEnableMiniDump=1", "-e", "COMPlus_DbgEnableMiniDump=1",
               "-e", "DOTNET_DbgMiniDumpName=/tmp/gated-boot.dmp",
               "-e", "COMPlus_DbgMiniDumpName=/tmp/gated-boot.dmp", image["Id"])
        docker("start", server)
        docker("wait", server, timeout=args.timeout)
        state = json.loads(docker("inspect", server))[0]["State"]
        # docker logs splits stdout/stderr; retain both streams for positive evidence.
        result = subprocess.run(["docker", "logs", server], capture_output=True, text=True, check=True, timeout=30)
        logs = result.stdout + result.stderr
        (args.output / "server.log").write_text(logs)
        changes = docker("diff", server).splitlines()
        dumps = [line[2:] for line in changes if line[:1] in ("A", "C") and is_dump(line[2:])]
        receipt.update(exit_code=state["ExitCode"], oom_killed=state["OOMKilled"], dumps=dumps,
                       refusal_observed=REFUSAL in logs, pending_contract_observed=CONTRACT in logs,
                       container_changes=changes)
        validate(state["ExitCode"], logs, dumps, state["OOMKilled"])
        journal = docker("exec", pg, "psql", "-At", "-U", "postgres", "-d", "honua_refusal",
                         "-c", "SELECT scriptname FROM public.schema_versions ORDER BY scriptname")
        receipt["journal_after"] = journal.splitlines()
        if journal != "Honua.Server.Migrations.002_CreateAttachmentsTable.sql":
            raise ValueError("refused boot changed the seeded migration journal")
        receipt["passed"] = True
        print(json.dumps(receipt, indent=2))
    except Exception as error:
        receipt["error"] = str(error)
        raise
    finally:
        # Preserve logs even on timeout; forcibly remove only this invocation's resources.
        try:
            result = subprocess.run(["docker", "logs", server], capture_output=True, text=True, timeout=30)
            if result.returncode == 0:
                (args.output / "server.log").write_text(result.stdout + result.stderr)
        finally:
            (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
            cleanup_errors = []
            for command in (("rm", "-f", server), ("rm", "-f", pg), ("network", "rm", network)):
                try:
                    subprocess.run(["docker", *command], capture_output=True, timeout=60, check=False)
                except (subprocess.TimeoutExpired, OSError) as error:
                    cleanup_errors.append(f"{command}: {error}")
            if cleanup_errors:
                receipt["cleanup_errors"] = cleanup_errors
                (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")


if __name__ == "__main__":
    main()
