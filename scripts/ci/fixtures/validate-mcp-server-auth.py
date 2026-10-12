#!/usr/bin/env python3
"""Check inherited MCP CI authentication against the real Development server action.

No server, database, credentials, or network is accessed. The regression composes
the workflow/job/action environment scopes that caused both live certification
transports to exit before migrations. Production authentication stays unchanged.
"""
from pathlib import Path
import re

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
