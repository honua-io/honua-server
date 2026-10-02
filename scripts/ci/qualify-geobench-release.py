"""Source-stamped managed correctness verification on an isolated hosted runner."""

import hashlib
import json
import os
from pathlib import Path
import subprocess
import sys
import xml.etree.ElementTree as ET


def sha256(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    spec_path = Path(__file__).with_suffix(".json")
    spec = json.loads(spec_path.read_text())
    source = os.environ["QUALIFICATION_SOURCE"]
    assert len(source) == 40 and all(c in "0123456789abcdef" for c in source)
    work = Path.cwd().resolve()
    actual = subprocess.check_output(["git", "rev-parse", "HEAD"], text=True).strip()
    assert actual == source, "Checkout does not match requested source"
    folder = work / "qualification-managed"
    folder.mkdir(exist_ok=False)
    receipt = {"source": source, "sdk": spec["SDK"], "spec_sha256": sha256(spec_path),
               "status": "running", "tests": [], "performance_measurement": False}

    def save():
        (folder / "receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")

    save()
    projects = list(dict.fromkeys(p for _, p, _ in spec["PROJECTS"]))
    (folder / "batch.slnf").write_text(json.dumps({"solution": {"path": "/src/Honua.sln", "projects": projects}}))
    props = ["-p:SelfContained=false", "-p:PublishAot=false", "-p:TreatWarningsAsErrors=true",
             "-p:UseSharedCompilation=false", "-p:BuildInParallel=false", "-p:HonuaIncludeStacOpsDemo=false",
             "-p:HonuaIncludeOracle=false", "-p:HonuaIncludeSnowflake=false",
             "-p:EnableSourceControlManagerQueries=false", "-p:SourceRevisionId=" + source]
    env = dict(os.environ)
    env["HONUA_TEST_DB_URL"] = "Host=127.0.0.1;Database=honua_test;Username=test;Password=qualification-only;SSL Mode=Disable"
    env["HONUA_TEST_REDIS_URL"] = "127.0.0.1:6379,abortConnect=false"
    name = "geobench-managed-" + env["GITHUB_RUN_ID"] + "-" + env["GITHUB_RUN_ATTEMPT"]
    identity = None
    try:
        identity = subprocess.check_output([
            "docker", "create", "--name", name, "--label", "io.honua.qualification=" + name,
            "--network", "host", "--cpus", "4", "--memory", "8g",
            "--mount", f"type=bind,src={work},dst=/src", "--workdir", "/src",
            "-e", "DOTNET_CLI_HOME=/tmp", "-e", "DOTNET_PROCESSOR_COUNT=4",
            "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1", "-e", "HONUA_TEST_DB_URL", "-e", "HONUA_TEST_REDIS_URL",
            "-e", "NuGetPackageSourceCredentials_github-honua", spec["SDK"], "sleep", "infinity"
        ], text=True, env=env).strip()
        receipt["container"] = identity
        save()
        subprocess.run(["docker", "start", identity], check=True)

        def execute(stage, args):
            receipt["stage"] = stage
            save()
            with (folder / (stage + ".log")).open("w") as log:
                result = subprocess.run(["docker", "exec", identity, *args], stdout=log, stderr=subprocess.STDOUT)
            print(stage + ": exit " + str(result.returncode), flush=True)
            if result.returncode:
                print((folder / (stage + ".log")).read_text()[-12000:], flush=True)
            return result.returncode

        assert execute("build", ["dotnet", "build", "/src/qualification-managed/batch.slnf", "-c", "Release", "-m:1", *props]) == 0, "Fresh managed build failed"
        for name, project, selection in spec["PROJECTS"]:
            code = execute("test-" + name, ["dotnet", "test", project, "-c", "Release", "--no-build", "--no-restore",
                           "--filter", selection, "--logger", "trx;LogFileName=" + name + ".trx",
                           "--results-directory", "/src/qualification-managed", *props])
            path = folder / (name + ".trx")
            root = ET.parse(path)
            cases = root.findall(".//{*}UnitTestResult")
            definitions = {d.get("id"): d.find("{*}TestMethod") for d in root.findall(".//{*}UnitTest")}
            methods = [definitions[c.get("testId")].get("name") for c in cases if c.get("outcome") == "Passed"]
            failed = [c.get("testName") for c in cases if c.get("outcome") == "Failed"]
            skipped = [c.get("testName") for c in cases if c.get("outcome") not in {"Passed", "Failed"}]
            receipt["tests"].append({"name": name, "selection": selection, "exit_code": code,
                                     "total": len(cases), "failed": failed, "skipped": skipped, "trx_sha256": sha256(path),
                                     "passed_methods": sorted(set(methods))})
            save()
            assert code == 0 and cases and not failed and not skipped, name + ": missing, failed or skipped tests"
            required = {"postgres": spec["REQUIRED_NATIVE"], "ogc-api": spec["REQUIRED_OGC"],
                        "core": ["Index_ServiceScopes_PreservesFirstWinsIdsAndCaseRules"],
                        "security": ["ResolveAsync_ValidatedSnapshot_ReusesScopesButReadsCurrentPoliciesAndPrincipal"]}.get(name, [])
            assert set(required) <= set(methods), name + ": required method coverage missing"
            if name == "ogc-api":
                assert methods.count("Create_PreservesVisibilityProjectionDatesAndIdentifiers") == 8, "Eight timestamp variants required"
            if name == "cloud-handoff":
                classes = [definitions[c.get("testId")].get("className", "") for c in cases]
                assert any("AwsBatchRequestCancellationTests" in c for c in classes)
                assert any("GpDeploymentHandoffTests" in c for c in classes)
        receipt["assembly_hashes"] = {str(p.relative_to(work)): sha256(p) for group in ("src/**", "tests/dotnet/**")
                                      for p in work.glob(group + "/bin/Release/net10.0/Honua*.dll")}
        assert receipt["assembly_hashes"], "Missing built assemblies"
        receipt["status"] = "passed-managed-correctness-needs-aot-http-oracle"
    except BaseException as error:
        receipt.update(status="failed", error_type=type(error).__name__, error=str(error))
        raise
    finally:
        save()
        if identity:
            inspected = json.loads(subprocess.check_output(["docker", "inspect", identity], text=True))[0]
            assert inspected["Config"]["Labels"].get("io.honua.qualification") == "geobench-managed-" + env["GITHUB_RUN_ID"] + "-" + env["GITHUB_RUN_ATTEMPT"]
            subprocess.run(["docker", "rm", "-f", identity], check=True)
            receipt["container_cleaned"] = identity
            save()


if __name__ == "__main__":
    main()
