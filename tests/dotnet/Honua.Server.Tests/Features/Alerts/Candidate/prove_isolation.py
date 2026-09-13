#!/usr/bin/env python3
# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.
"""Exercise the Preview isolation floor on an immutable, already-built image.

This is NOT concurrent tenant evaluation qualification. The shipped worker
startup restriction prevents that scenario. Retain a failing receipt on errors.
Only disposable containers created by this invocation are removed.
"""
import argparse
import hashlib
import json
import re
import subprocess
import time
import urllib.error
import urllib.request
import uuid
from pathlib import Path

DENIAL = "Preview alert administration requires an instance administrator"
STARTUP_DENIAL = "Alert processing cannot run with tenant resolution or schema routing enabled"


def command(*args, input=None):
    return subprocess.run(args, input=input, text=True, capture_output=True, check=True).stdout.strip()


def require(condition, message):
    if not condition:
        raise AssertionError(message)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--image", required=True)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--output", required=True, type=Path)
    args = parser.parse_args()
    require(re.fullmatch(r".+@sha256:[0-9a-f]{64}", args.image), "Supply the manifest image@digest")
    require(re.fullmatch(r"[0-9a-f]{40}", args.sha), "Supply the full manifest source SHA")
    args.output.mkdir(parents=True, exist_ok=False)
    prefix = "alerts-3859-" + uuid.uuid4().hex[:10]
    containers = []
    receipt = {"image": args.image, "sourceSha": args.sha, "outcome": "fail",
               "scope": "Preview startup and tenant-header HTTP refusal only",
               "concurrentTenantEvaluation": "not qualified: tenant-owned alerts are not implemented",
               "checks": [], "http": []}
    password = uuid.uuid4().hex
    correlation = uuid.uuid4().hex
    network_created = False

    def run(name, image, env=None, extra=()):
        name = prefix + "-" + name
        cmd = ["docker", "run", "-d", "--name", name, "--network", prefix]
        for key, value in (env or {}).items():
            cmd += ["-e", key + "=" + value]
        containers.append(name)
        command(*cmd, *extra, image)
        return name

    def sql(statement):
        return command("docker", "exec", "-i", postgres, "psql", "-XAt", "-v", "ON_ERROR_STOP=1",
                       "-h", "127.0.0.1", "-U", "honua", "-d", "honua", input=statement)

    def start_server(name, **settings):
        return run(name, args.image, {
            "ASPNETCORE_ENVIRONMENT": "Development", "HONUA_DEV_AUTH": "false",
            "HONUA_DEV_AUTH_ALLOW_BYPASS": "false", "HONUA_ADMIN_PASSWORD": password,
            "ConnectionStrings__DefaultConnection": f"Host={postgres};Database=honua;Username=honua;Password={password}",
            "ConnectionStrings__Redis": redis + ":6379", "Licensing__DevGrantEdition": "Enterprise",
            "Security__ConnectionEncryption__MasterKey": "alerts-proof-local-master-key-0123456789",
            "Security__ConnectionEncryption__Salt": "aG9udWEtZ3AtcmVsaWFiaWxpdHktc2FsdA==",
            "Kestrel__Endpoints__Http__Url": "http://+:8080",
            "Kestrel__Endpoints__Http__Protocols": "Http1",
            "Capabilities__Experimental__alerts.geofence__Enabled": "true",
            **settings}, ["--memory", "1g", "--cpus", "1", "-p", "127.0.0.1::8080"])

    def request(method, path, tenant=None, authenticated=True, body=None):
        headers = {"Content-Type": "application/json"}
        if authenticated:
            headers["X-API-Key"] = password
        if tenant:
            headers["X-Honua-Tenant"] = tenant
            headers["X-Correlation-ID"] = correlation
        data = json.dumps(body or {}).encode() if method not in ("GET", "DELETE") else None
        req = urllib.request.Request(base + path, headers=headers, data=data, method=method)
        try:
            with urllib.request.urlopen(req, timeout=20) as response:
                status, text = response.status, response.read().decode()
        except urllib.error.HTTPError as error:
            status, text = error.code, error.read().decode()
        receipt["http"].append({"method": method, "path": path, "tenant": tenant,
                                "authenticated": authenticated, "status": status, "body": text})
        return status, text

    try:
        info = json.loads(command("docker", "image", "inspect", args.image))[0]
        require(info["Config"]["Labels"]["org.opencontainers.image.revision"] == args.sha,
                "Image revision does not match the release manifest")
        receipt["imageId"] = info["Id"]
        command("docker", "network", "create", prefix)
        network_created = True
        postgres = run("postgres", "postgis/postgis:18-3.6", {
            "POSTGRES_USER": "honua", "POSTGRES_DB": "honua", "POSTGRES_PASSWORD": password})
        redis = run("redis", "redis:7.2-alpine")
        for _ in range(90):
            try:
                sql("SELECT 1;")
                break
            except subprocess.CalledProcessError:
                time.sleep(1)
        else:
            raise AssertionError("Postgres did not become ready")
        # Matches the supported quickstart database initialization.
        root = Path(__file__).resolve().parents[6]
        sql((root / "docker/init-db.sql").read_text())
        server = start_server("control", Alerts__Enabled="false", MultiTenancy__Enabled="false")
        port = command("docker", "port", server, "8080/tcp").split(":")[-1]
        base = "http://127.0.0.1:" + port
        for _ in range(120):
            try:
                if request("GET", "/healthz/ready")[0] == 200:
                    break
            except (OSError, TimeoutError):
                pass
            time.sleep(1)
        else:
            raise AssertionError("Single-tenant control did not become ready")
        receipt["checks"].append("single-tenant control ready")
        # Seed independently specified instance data, not output captured from the server.
        sql("""
INSERT INTO honua.alert_zones(zone_id,service_id,zone_name,geometry)
VALUES(3859,'private-instance-3859','original',ST_GeomFromText('MULTIPOLYGON(((0 0,0 2,2 2,2 0,0 0)))',4326));
INSERT INTO honua.alert_rules(rule_id,service_id,layer_id,zone_id,rule_name,trigger_type,channels)
VALUES(3859,'private-instance-3859',1,3859,'original',4,ARRAY['webhook']);
INSERT INTO honua.alert_events(event_id,dedupe_key,rule_id,zone_id,service_id,layer_id,objectid,trigger_type,generation,severity)
VALUES(3859,'fixture-3859',3859,3859,'private-instance-3859',1,42,4,1,'warning');
INSERT INTO honua.alert_event_lifecycle(event_id) VALUES(3859);
INSERT INTO honua.alert_dispatch(event_id,channel_type,destination,status,attempts)
VALUES(3859,1,'https://private-receiver.invalid/3859',4,3);
INSERT INTO honua.alert_channel_state(channel_type,is_paused) VALUES(1,true);
""")
        tables = ["alert_zones", "alert_rules", "alert_events", "alert_event_lifecycle",
                  "alert_dispatch", "alert_channel_state", "alert_state", "alert_worker_checkpoint"]

        def rows():
            return {table: json.loads(sql(f"SELECT coalesce(jsonb_agg(to_jsonb(t) ORDER BY to_jsonb(t)::text),'[]') FROM honua.{table} t;"))
                    for table in tables}

        before = rows()
        # Reuse #4606's maintained route denominator; its C# test additionally discovers
        # live EndpointDataSource routes. Never claim source parsing discovers image routes.
        source = (Path(__file__).parents[1] / "AlertTenantIsolationRouteCoverageProofTests.cs").read_text()
        routes = re.findall(r'"((?:GET|POST|PUT|DELETE) /api/v\{version:apiVersion\}/admin/[^"\n]+)"',
                            source.split("private static readonly string[] KnownAlertRoutes =", 1)[1].split("];", 1)[0])
        require(len(routes) >= 23, "Route denominator unexpectedly shrank")
        for route in routes:
            method, path = route.split(" ", 1)
            path = re.sub(r"\{([^}:]+)(?::[^}]+)?\}",
                          lambda match: {"version": "1", "zoneId": "3859", "ruleId": "3859",
                                         "eventId": "3859", "channel": "webhook"}[match[1]], path)
            rule = {"serviceId": "private-instance-3859", "layerId": 1, "zoneId": None,
                    "ruleName": "attempted", "triggerType": "threshold",
                    "conditionsJson": '{"field":"speed","operator":">","value":30}',
                    "cooldownSeconds": 60, "severity": "warning", "editionRequired": "pro",
                    "channels": ["webhook"], "isActive": True}
            body_input = {"note": "3859"}
            if "/zones" in path:
                body_input = {"serviceId": "private-instance-3859", "zoneName": "attempted",
                              "wkt": "POLYGON((0 0,0 2,2 2,2 0,0 0))", "srid": 4326, "isActive": True}
            elif path.endswith("/rules/test"):
                body_input = {"rule": rule}
            elif path.endswith("/enabled"):
                body_input = {"enabled": True}
            elif "/rules" in path:
                body_input = rule
            elif path.endswith("/suppress"):
                body_input = {"suppressUntil": "2099-01-01T00:00:00Z", "note": "3859"}
            for tenant in ("tenant-a-3859", "tenant-b-3859"):
                status, body = request(method, path, tenant, body=body_input)
                require(status == 403 and DENIAL in body, f"Isolation filter did not refuse {tenant} {route}: {status} {body}")
                for private in (tenant, "private-instance-3859", "private-receiver.invalid"):
                    require(private not in body, f"Refusal disclosed {private}")
            status, _ = request(method, path, "tenant-unauthorized-3859", authenticated=False, body=body_input)
            require(status in (401, 403), f"Unauthenticated request was not denied: {route}")
        after = rows()
        require(after == before, "Tenant requests mutated alert persistence")
        geometry = json.loads(sql("SELECT json_build_object('area',ST_Area(geometry),'srid',ST_SRID(geometry),'geometry',ST_AsGeoJSON(geometry)::json) FROM honua.alert_zones WHERE zone_id=3859;"))
        require(geometry == {"area": 4, "srid": 4326, "geometry": {"type": "MultiPolygon", "coordinates": [[[[0, 0], [0, 2], [2, 2], [2, 0], [0, 0]]]]}}, "Fixture geometry changed")
        # A real successful control defeats vacuous blanket authorization/capability denial.
        status, body = request("GET", "/api/v1/admin/alerts/zones?serviceId=private-instance-3859")
        require(status == 200 and "original" in body, "Instance administrator cannot read seeded zone")
        audit = json.loads(sql(f"SELECT coalesce(jsonb_agg(jsonb_build_object('resourceType',resource_type,'outcome',outcome,'details',details)),'[]') FROM honua.audit_log WHERE correlation_id='{correlation}';"))
        require(len(audit) >= 2 * len(routes), "Tenant refusals lack access-audit records")
        for row in audit:
            require(row["outcome"] != "Success", "Refused request audited as success")
            require(row["resourceType"] not in ("alert_zone", "alert_rule", "alert-channel", "alert_event"),
                    "Tenant request reached alert-domain audit mutation")
            require("private-instance-3859" not in row["details"] and "private-receiver.invalid" not in row["details"],
                    "Refusal audit disclosed private alert data")
        receipt["audit"] = audit
        receipt["rows"] = after
        receipt["geometry"] = geometry
        receipt["checks"].append(f"{len(routes)} routes refuse both tenant headers and unauthenticated calls without mutation")
        command("docker", "stop", server)
        for name, settings in (
            ("default-tenant", {}),
            ("explicit-tenant", {"MultiTenancy__Enabled": "true"}),
            ("schema-routing", {"MultiTenancy__Enabled": "false", "MultiTenancy__SchemaRouting__Enabled": "true"}),
        ):
            worker = start_server(name, Alerts__Enabled="true", **settings)
            for _ in range(90):
                state = json.loads(command("docker", "inspect", worker))[0]["State"]
                if not state["Running"]:
                    break
                time.sleep(1)
            else:
                raise AssertionError(f"{name}: tenant-enabled workers did not exit")
            # Native startup errors may be written to stderr.
            log_result = subprocess.run(["docker", "logs", worker], capture_output=True, text=True, check=True)
            logs = log_result.stdout + log_result.stderr
            require(state["ExitCode"] != 0 and STARTUP_DENIAL in logs, f"{name}: not rejected by the isolation validator")
            require(rows() == before, f"{name}: rejected startup mutated alert data")
            receipt["checks"].append(name + ": startup rejected before evaluation/delivery mutation")
        receipt["outcome"] = "pass"
    except Exception as error:
        receipt["error"] = (str(error) + "\n" + (getattr(error, "stderr", "") or "")).replace(password, "[fixture-credential]")
        raise
    finally:
        for name in reversed(containers):
            result = subprocess.run(["docker", "logs", name], capture_output=True, text=True)
            log = (result.stdout + result.stderr).replace(password, "[fixture-credential]")
            (args.output / (name.removeprefix(prefix + "-") + ".log")).write_text(log)
            result = subprocess.run(["docker", "rm", "-f", "-v", name], capture_output=True, text=True)
            if result.returncode:
                receipt["outcome"] = "fail"
                receipt["cleanupError"] = result.stderr
        if network_created:
            result = subprocess.run(["docker", "network", "rm", prefix], capture_output=True, text=True)
            if result.returncode:
                receipt["outcome"] = "fail"
                receipt["cleanupError"] = result.stderr
        (args.output / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
        (args.output / "SHA256SUMS").write_text("".join(
            f"{hashlib.sha256(path.read_bytes()).hexdigest()}  {path.name}\n"
            for path in sorted(args.output.iterdir()) if path.name != "SHA256SUMS"))
    require(receipt["outcome"] == "pass", "Candidate proof failed")


if __name__ == "__main__":
    main()
