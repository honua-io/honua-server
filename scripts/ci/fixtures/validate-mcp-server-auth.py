#!/usr/bin/env python3
"""Check inherited MCP CI authentication against the real Development server action.

No server, database, credentials, or network is accessed. The regression composes
the workflow/job/action environment scopes that caused both live certification
transports to exit before migrations. Production authentication stays unchanged.
"""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import textwrap
import time

ROOT = Path(__file__).resolve().parents[3]
WORKFLOW = (ROOT / ".github/workflows/ci.yml").read_text(encoding="utf-8")
SERVER = (ROOT / ".github/actions/setup-honua-server/action.yml").read_text(encoding="utf-8")
MCP = (ROOT / ".github/actions/setup-honua-mcp/action.yml").read_text(encoding="utf-8")


def environment(block, indent):
    matches = list(re.finditer(r"(?m)^" + " " * indent + r"env:\s*$", block))
    assert len(matches) <= 1, "Ambiguous environment scope"
    if not matches:
        return {}
    result = {}
    for line in block[matches[0].end():].splitlines():
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        match = re.fullmatch(" " * (indent + 2) + r"([A-Za-z_][A-Za-z0-9_]*):\s*(.*?)\s*", line)
        if not match:
            break
        result[match[1]] = match[2].strip("\"'")
    return result


def named_job(source, name):
    match = re.search(r"(?m)^  " + re.escape(name) + r":\s*$", source)
    assert match, f"Missing MCP job: {name}"
    return re.split(r"(?m)^  [a-z][a-z0-9-]*:\s*$", source[match.end():], maxsplit=1)[0]


def check(workflow, server=SERVER, mcp=MCP):
    setup = re.search(r"(?ms)^    - name: Setup Honua Server\n(.*?)(?=^    - |\Z)", mcp)
    assert setup and "uses: ./.github/actions/setup-honua-server" in setup[0]
    start = re.search(r"(?ms)^    - name: Start Honua Server\n(.*?)(?=^    - |\Z)", server)
    assert start and "dotnet run --project src/Honua.Server" in start[0]
    action_env = environment(start[0], 6)
    assert action_env.get("ASPNETCORE_ENVIRONMENT") == "Development", "MCP must exercise the Development server"
    admin = action_env.get("HONUA_ADMIN_PASSWORD")
    assert admin, "The shared server must require an explicit admin credential"
    for name in ("mcp-certification", "mcp-llm-smoke"):
        job = named_job(workflow, name)
        steps = re.split(r"(?m)^      - ", job)[1:]
        setup_steps = [step for step in steps if "uses: ./.github/actions/setup-honua-mcp" in step]
        assert len(setup_steps) == 1, f"{name}: shared MCP setup must be preserved"
        inherited = {**environment(workflow, 0), **environment(job, 4)}
        startup = {**inherited, **environment(setup_steps[0], 8), **environment(setup[0], 6), **action_env}
        assert not startup.get("HONUA_DEV_AUTH"), f"{name}: forbidden legacy DEV_AUTH reaches Development startup"
        assert not startup.get("HONUA_DEV_AUTH_ALLOW_BYPASS"), f"{name}: authentication bypass must stay disabled"
        run_name = "Run MCP Certification Tests" if name == "mcp-certification" else "Run MCP LLM Smoke Tests"
        selected = [step for step in steps if step.startswith("name: " + run_name + "\n")]
        assert len(selected) == 1, f"{name}: original native MCP test command must remain"
        client = {**inherited, **environment(selected[0], 8)}
        assert client.get("HONUA_API_KEY") == admin, f"{name}: client credential must match server authentication"
        assert not client.get("HONUA_DEV_AUTH"), f"{name}: client must use the real API-key path"


def rejected(label, workflow, server=SERVER):
    try:
        check(workflow, server)
    except AssertionError:
        print("PASS: rejected " + label)
    else:
        raise AssertionError("Accepted invalid MCP authentication: " + label)


check(WORKFLOW)
print("PASS: real MCP certification and smoke environments preserve Development API-key authentication")
certification = named_job(WORKFLOW, "mcp-certification")
rejected("job-level legacy bypass", WORKFLOW.replace(certification,
    certification.replace("      HONUA_API_KEY:", '      HONUA_DEV_AUTH: "true"\n      HONUA_API_KEY:', 1)))
rejected("workflow-level inherited bypass", WORKFLOW.replace("\nenv:\n", '\nenv:\n  HONUA_DEV_AUTH: "true"\n', 1))
rejected("smoke client legacy bypass", WORKFLOW.replace('          HONUA_API_KEY: "ci-admin-password"', '          HONUA_DEV_AUTH: "true"\n          HONUA_API_KEY: "ci-admin-password"'))
rejected("missing server credential", WORKFLOW, SERVER.replace('        HONUA_ADMIN_PASSWORD: "ci-admin-password"', '        HONUA_ADMIN_PASSWORD: ""'))
rejected("client-server credential mismatch", WORKFLOW.replace('HONUA_API_KEY: "ci-admin-password"', 'HONUA_API_KEY: "different-admin"'))
rejected("test-environment substitution", WORKFLOW, SERVER.replace('ASPNETCORE_ENVIRONMENT: "Development"', 'ASPNETCORE_ENVIRONMENT: "Test"'))
print("PASS: MCP server-auth environment regression (7 checks)")

SDK_NODE = "22.23.3"
HEAP = "NODE_OPTIONS: --max-old-space-size=4096"
build = re.search(
    r"(?ms)^    - name: Build the selected SDK source for the MCP file peer\n(.*?)(?=^    - |\Z)",
    MCP,
)
assert build and HEAP in build[0], "MCP SDK build must keep the 4 GiB V8 heap"
for job_name in ("mcp-certification", "mcp-llm-smoke"):
    job = named_job(WORKFLOW, job_name)
    assert f"node-version: '{SDK_NODE}'" in job, f"{job_name} must build the SDK on Node {SDK_NODE}"
focused = (ROOT / ".github/workflows/mcp-certification-focused.yml").read_text(encoding="utf-8")
assert f"node-version: '{SDK_NODE}'" in named_job(focused, "certification"), (
    "focused MCP certification must build the SDK on Node " + SDK_NODE
)
print("PASS: MCP SDK build uses Node 22.23.3 and a 4 GiB heap")


def check_smoke_admission(workflow):
    job = named_job(workflow, "mcp-llm-smoke")
    steps = re.split(r"(?m)^      - ", job)[1:]
    admission = steps[0]
    assert admission.startswith("name: Detect Bedrock smoke credentials\n"), "Admit credentials before checkout or setup"
    assert "HONUA_BEDROCK_SMOKE_ROLE_ARN != '' &&" in job, "Do not start PostgreSQL without credentials"
    assert re.search(r"(?m)^        id: bedrock$", admission), "Smoke steps must reference the admission ID"
    assert (
        "        env:\n          BEDROCK_ROLE_ARN: ${{ vars.HONUA_BEDROCK_SMOKE_ROLE_ARN }}\n" in admission
    ), "Admission must read the repository's Bedrock role"
    credential_guard = "steps.bedrock.outputs.available == 'true'"
    script_guard = "steps.setup.outputs.smoke-available"
    expected_guards = {
        "uses: actions/checkout@": credential_guard,
        "name: Configure PostgreSQL connection": credential_guard,
        "name: Setup Honua MCP environment": credential_guard,
        "name: Report skipped smoke tests": f"{credential_guard} && {script_guard} != 'true'",
        "name: Configure AWS credentials for Bedrock smoke": f"{credential_guard} && {script_guard} == 'true'",
        "name: Run MCP LLM Smoke Tests": f"{credential_guard} && {script_guard} == 'true'",
        "name: Upload LLM smoke transcripts": f"always() && {credential_guard} && {script_guard} == 'true'",
    }
    selected = {}
    for step_name, guard in expected_guards.items():
        matching_steps = [step for step in steps[1:] if step.startswith(step_name)]
        assert len(matching_steps) == 1, f"Expected one smoke step beginning with {step_name}"
        assert f"        if: {guard}" in matching_steps[0].splitlines(), f"Unexpected guard for {step_name}"
        selected[step_name] = matching_steps[0]
    for step in steps[1:]:
        assert re.search(rf"(?m)^        if: .*{re.escape(credential_guard)}", step), (
            "Every expensive smoke step must require credentials"
        )
    setup = selected["name: Setup Honua MCP environment"]
    skip = selected["name: Report skipped smoke tests"]
    smoke = selected["name: Run MCP LLM Smoke Tests"]
    assert "sdk-ref: ${{ github.event.inputs.sdk_ref || env.MCP_SDK_REF }}" in setup
    check_smoke_only_setup(setup)
    assert "npm run test:llm-smoke" in smoke, "Preserve the credentialed smoke command"
    assert "needs: mcp-certification" in job and "continue-on-error: true" in job
    check_smoke_timeouts(job, selected, [step for step in steps if step != skip])
    return admission, skip, smoke


def check_smoke_only_setup(setup):
    assert "          smoke-only: 'true'" in setup.splitlines(), "Skip heavy setup when only certification is available"
    heavy_steps = MCP.split("    - name: Setup Python\n", 1)[1]
    guard = "steps.check-scripts.outputs.smoke == 'true' || (inputs.smoke-only != 'true' && steps.check-scripts.outputs.cert == 'true')"
    for step in re.split(r"(?m)^    - name: ", heavy_steps):
        assert f"      if: {guard}" in step.splitlines(), "Every heavy step must honor smoke-only admission"


def check_smoke_timeouts(job, selected, credentialed_steps):
    setup = selected["name: Setup Honua MCP environment"]
    smoke = selected["name: Run MCP LLM Smoke Tests"]
    upload = selected["name: Upload LLM smoke transcripts"]
    observed_setup_and_service_seconds = 594
    upload_limit_minutes = 2
    minimum_reserve_minutes = 4
    assert minutes(setup, 8) * 60 > observed_setup_and_service_seconds, (
        "Setup must fit the measured setup plus service startup"
    )
    check_smoke_command_budget(smoke)
    assert minutes(upload, 8) <= upload_limit_minutes, "Bound transcript upload"
    credentialed_minutes = sum(minutes(step, 8) for step in credentialed_steps)
    assert minutes(job, 4) - credentialed_minutes >= minimum_reserve_minutes, "Reserve time for services and cleanup"


def check_smoke_command_budget(smoke):
    smoke_duration = environment(smoke, 8)["MCP_SMOKE_TIMEOUT"]
    duration = re.fullmatch(r"([1-9][0-9]*)m", smoke_duration)
    assert duration, "Require a positive smoke command timeout in minutes"
    grace = re.search(r"--kill-after=([1-9][0-9]*)s\b", smoke)
    assert grace, "Require a positive termination grace in seconds"
    assert int(duration[1]) * 60 + int(grace[1]) < minutes(smoke, 8) * 60, (
        "Allow forced termination before the step deadline"
    )


def minutes(block, indent):
    match = re.search(rf"(?m)^ {{{indent}}}timeout-minutes: ([1-9][0-9]*)$", block)
    assert match, "Expected a positive workflow timeout"
    return int(match[1])


def run_script(script, directory, variables):
    return subprocess.run(
        ["bash", "-euo", "pipefail", "-c", textwrap.dedent(script)],
        cwd=directory,
        env={"PATH": os.environ["PATH"], "HOME": str(directory), **variables},
        capture_output=True, text=True, timeout=15,
    )


def exercise_smoke_admission(admission):
    script = admission.split("        run: |\n", 1)[1]
    for role, expected in (("", "false"), ("arn:aws:iam::123456789012:role/smoke", "true")):
        with tempfile.TemporaryDirectory() as directory:
            output = Path(directory) / "output"
            summary = Path(directory) / "summary"
            result = run_script(script, directory, {
                "BEDROCK_ROLE_ARN": role, "GITHUB_OUTPUT": str(output), "GITHUB_STEP_SUMMARY": str(summary),
            })
            result.check_returncode()
            assert output.read_text().strip() == "available=" + expected
            if not role:
                assert "skipped" in summary.read_text().lower(), "Explain absent credentials in the job summary"
                assert "::notice::" in result.stdout, "Explain absent credentials in the log"
            else:
                assert not summary.exists(), "Credentialed smoke must proceed without a skip report"


def exercise_smoke_script(skip, smoke):
    detect = re.search(r"(?ms)^    - name: Check SDK MCP scripts\n(.*?)(?=^    - )", MCP)
    assert detect, "Missing SDK MCP script detection"
    detect_command = detect[1].split("      run: |\n", 1)[1]
    skip_command = skip.split("        run: |\n", 1)[1]
    smoke_command = re.search(r"(?m)^        run: (.+)$", smoke)[1]
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        mcp = root / "honua-sdk-js/mcp"
        mcp.mkdir(parents=True)
        package = mcp / "package.json"
        output = root / "output"
        summary = root / "summary"

        package.write_text(json.dumps({"scripts": {"test:certification": "true"}}))
        run_script(detect_command, root, {"GITHUB_OUTPUT": str(output)}).check_returncode()
        assert "smoke=false" in output.read_text().splitlines()
        run_script(skip_command, root, {
            "SDK_REF": "fixture-sdk-override", "GITHUB_STEP_SUMMARY": str(summary),
        }).check_returncode()
        assert "fixture-sdk-override" in summary.read_text()

        for script, expected in (("echo smoke-started", 0), ("echo smoke-started && sleep 60", 124)):
            package.write_text(json.dumps({"scripts": {"test:llm-smoke": script}}))
            output.unlink()
            run_script(detect_command, root, {"GITHUB_OUTPUT": str(output)}).check_returncode()
            assert "smoke=true" in output.read_text().splitlines()
            result = run_script(smoke_command, mcp, {"MCP_SMOKE_TIMEOUT": "2s"})
            assert "smoke-started" in result.stdout.splitlines(), result.stderr
            assert result.returncode == expected, (result.returncode, result.stderr)

        exercise_forced_smoke_termination(smoke_command, root, mcp)


def exercise_forced_smoke_termination(smoke_command, root, mcp):
    smoke_timeout_seconds = 1
    termination_grace_seconds = 10
    stubborn_npm = root / "npm"
    stubborn_npm.write_text("#!/bin/sh\ntrap '' TERM\necho smoke-started\nexec sleep 60\n")
    stubborn_npm.chmod(0o755)
    grace = re.search(r"--kill-after=(\d+)s\b", smoke_command)
    assert grace and int(grace[1]) == termination_grace_seconds, "Preserve the smoke command's termination grace"
    start = time.monotonic()
    result = run_script(smoke_command, mcp, {
        "MCP_SMOKE_TIMEOUT": f"{smoke_timeout_seconds}s", "PATH": f"{root}:{os.environ['PATH']}",
    })
    assert "smoke-started" in result.stdout.splitlines()
    assert result.returncode in (-9, 137), (result.returncode, result.stderr)
    assert time.monotonic() - start >= smoke_timeout_seconds + termination_grace_seconds, (
        "SIGKILL fired before the grace period elapsed"
    )


admission_step, skip_step, smoke_step = check_smoke_admission(WORKFLOW)
exercise_smoke_admission(admission_step)
exercise_smoke_script(skip_step, smoke_step)
print("PASS: MCP smoke handles absent role, absent script, successful command, normal timeout and forced termination offline")
