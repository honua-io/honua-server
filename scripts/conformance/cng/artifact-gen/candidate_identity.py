"""Resolve the CNG qualification identity from the frozen release manifest."""

from __future__ import annotations

import json
import os
import re
import sys
from datetime import datetime
from pathlib import Path


def validate_inputs(image: str, source: str, cut_at: str) -> dict[str, str]:
    if not re.fullmatch(r"ghcr\.io/honua-io/honua-server@sha256:[0-9a-f]{64}", image):
        raise ValueError("candidate image must be an immutable honua-server registry digest")
    if not re.fullmatch(r"[0-9a-f]{40}", source):
        raise ValueError("candidate source must be a full commit SHA")
    parsed = datetime.fromisoformat(cut_at.replace("Z", "+00:00"))
    if parsed.tzinfo is None:
        raise ValueError("candidate cut timestamp must include a timezone")
    return {"server_image": image, "source_sha": source, "candidate_cut_at": cut_at}


def resolve(manifest: dict) -> dict[str, str]:
    server = manifest["components"]["honua-server"]
    source = server["sha"]
    if manifest["candidate"]["ref"] != source:
        raise ValueError("candidate ref differs from the server source")
    if manifest["candidate"]["refSource"] != "trunk":
        raise ValueError("candidate must originate from trunk")
    certification = manifest["protocolCertification"]
    if certification["serverCertificationProducerSha"] != source:
        raise ValueError("certification producer differs from the server source")
    repository = server["image"].split("@", 1)[0].split(":", 1)[0]
    return validate_inputs(
        repository + "@" + server["digest"], source, certification["candidateCutAt"]
    )


def main() -> None:
    if sys.argv[1:] == ["validate-inputs"]:
        result = validate_inputs(os.environ["SERVER_IMAGE"], os.environ["SOURCE_SHA"], os.environ["CUT_AT"])
    else:
        import yaml

        result = resolve(yaml.safe_load(Path(sys.argv[1]).read_text(encoding="utf-8")))
    print(json.dumps(result, sort_keys=True))


if __name__ == "__main__":
    main()
