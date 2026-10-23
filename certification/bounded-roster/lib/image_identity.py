"""Fail closed when a client base cannot be reproduced from a registry digest."""
import re


def pinned_base_digests(image: str) -> list[str]:
    if not re.fullmatch(r"[^\s@]+@sha256:[0-9a-f]{64}", image):
        raise ValueError(f"Client base must be pinned by registry digest: {image!r}")
    return [image]
