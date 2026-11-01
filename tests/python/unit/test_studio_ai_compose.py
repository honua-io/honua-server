"""Expand the opt-in assistant deployment without starting containers or calling AWS."""
import json
import os
from pathlib import Path
import subprocess

import pytest

ROOT = Path(__file__).resolve().parents[3]


def compose(*, overlay=True, model=True, deployment=True):
    environment = {
        "PATH": os.environ["PATH"],
        "HOME": os.environ["HOME"],
        "POSTGRES_PASSWORD": "fixture-postgres-password-only",
        "MINIO_ROOT_PASSWORD": "fixture-minio-password-only",
        "HONUA_ADMIN_PASSWORD": "fixture-admin-password-only",
        "HONUA_STUDIO_AI_REGION": "us-west-2",
    }
    if model:
        environment["HONUA_STUDIO_AI_MODEL"] = "fixture-model"
    if deployment:
        environment["HONUA_AI_DEPLOYMENT_ID"] = "fixture-deployment"
    command = ["docker", "compose", "--profile", "console", "--env-file", "/dev/null", "-f", str(ROOT / "docker-compose.yml")]
    if overlay:
        command += ["-f", str(ROOT / "docker-compose.studio-ai.yml")]
    return subprocess.run(command + ["config", "--format", "json"], env=environment,
                          cwd=ROOT, capture_output=True, text=True, check=False)


def test_assistant_overlay_expands_the_complete_bedrock_boundary():
    result = compose()
    assert result.returncode == 0, result.stderr
    services = json.loads(result.stdout)["services"]
    assert services["console"]["environment"]["Studio__EmbedJsStudio"] == "true"
    assert services["console"]["environment"]["Honua__Agent__WorkspaceApiEnabled"] == "false"
    settings = services["honua"]["environment"]
    assert settings["Studio__EndUserAuthorization__Enabled"] == "true"
    assert settings["StudioAiProxy__Enabled"] == "true"
    assert settings["StudioAiProxy__Providers__bedrock__Kind"] == "bedrock"
    assert settings["StudioAiProxy__Providers__bedrock__Model"] == "fixture-model"
    assert settings["StudioAiProxy__Disclosure__Approvals__0__Model"] == "fixture-model"
    assert settings["StudioAiProxy__Disclosure__Approvals__0__Destination"] == "aws-bedrock:us-west-2"
    assert settings["StudioAiProxy__Disclosure__Approvals__0__UseCase"] == "studio-chat"
    assert settings["AiExecutionAdmission__DeploymentId"] == "fixture-deployment"
    assert settings["AiExecutionAdmission__Backend"] == "postgres"


@pytest.mark.parametrize("missing", ["model", "deployment"])
def test_assistant_overlay_requires_explicit_model_and_stable_deployment(missing):
    result = compose(**{missing: False})
    assert result.returncode != 0
    assert ("HONUA_STUDIO_AI_MODEL" if missing == "model" else "HONUA_AI_DEPLOYMENT_ID") in result.stderr


def test_default_quickstart_does_not_enable_model_spending():
    result = compose(overlay=False)
    assert result.returncode == 0, result.stderr
    settings = json.loads(result.stdout)["services"]["honua"]["environment"]
    assert settings.get("StudioAiProxy__Enabled", "false") == "false"
