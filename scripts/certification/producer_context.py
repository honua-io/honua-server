"""Shared bounded certification dispatch data and exact workflow/run admission."""
import json
import os
import re
from pathlib import Path

SCHEMA = 'honua.protocol-producer-context/v1'


def load_envelope(path, producer, source_sha, producer_sha, image_digest, cut_at):
    raw = Path(path).read_bytes()
    if len(raw) > 60000:
        raise ValueError('requirements context exceeds 60000 bytes')
    def pairs(values):
        result = {}
        for key, value in values:
            if key in result:
                raise ValueError('requirements context contains duplicate JSON keys')
            result[key] = value
        return result
    value = json.loads(raw, object_pairs_hook=pairs)
    if not isinstance(value, dict) or set(value) != {'schema', 'requirements_revision', 'producer', 'producer_source_sha', 'candidate', 'requirements'}:
        raise ValueError('requirements context has unknown or missing fields')
    if value['schema'] != SCHEMA or value['producer'] != producer or value['producer_source_sha'] != producer_sha:
        raise ValueError('requirements context producer/source mismatch')
    if value['candidate'] != {'source_sha': source_sha, 'image_digest': image_digest, 'cut_at': cut_at}:
        raise ValueError('requirements context candidate mismatch')
    if not isinstance(value['requirements_revision'], str) or not re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9._/-]{0,199}', value['requirements_revision']):
        raise ValueError('requirements context has invalid catalog revision')
    return value


def run_identity(source_sha: str, workflow_name: str) -> dict:
    repository = os.environ.get("GITHUB_REPOSITORY")
    ref = os.environ.get("GITHUB_REF")
    workflow = ".github/workflows/" + workflow_name
    if (repository != "honua-io/honua-server" or os.environ.get("GITHUB_SHA") != source_sha
            or ref not in {"refs/heads/trunk", "refs/tags/nightly-candidate/" + source_sha}
            or os.environ.get("GITHUB_WORKFLOW_REF") != f"{repository}/{workflow}@{ref}"
            or os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch"):
        raise ValueError("Protocol certification requires the exact reviewed source and workflow ref")
    run_id, attempt = os.environ.get("GITHUB_RUN_ID", ""), os.environ.get("GITHUB_RUN_ATTEMPT", "")
    dispatch_id = os.environ.get("NIGHTLY_DISPATCH_ID", "")
    if not re.fullmatch(r"[1-9][0-9]*", run_id) or not re.fullmatch(r"[1-9][0-9]*", attempt) or not re.fullmatch(r"nightly-certification-[1-9][0-9]*-[1-9][0-9]*", dispatch_id):
        raise ValueError("Protocol certification requires exact run, attempt and nightly dispatch identity")
    return {"repository": repository, "workflow": workflow_name, "run_id": int(run_id),
            "run_attempt": int(attempt), "dispatch_id": dispatch_id}


