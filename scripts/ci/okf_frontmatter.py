"""Lossless metadata reader shared by the OKF gate and agent projections.

Uses safe YAML, preserves extension fields and datetime spellings, and refuses
duplicate keys rather than silently changing a concept's meaning.
"""
from __future__ import annotations

import yaml


class MetadataError(ValueError):
    pass


class Loader(yaml.SafeLoader):
    pass


# Metadata timestamps stay strings for deterministic JSON export. Do not mutate
# PyYAML's global SafeLoader or reinterpret YAML dates in unrelated consumers.
Loader.yaml_implicit_resolvers = {
    key: [(tag, pattern) for tag, pattern in values if tag != "tag:yaml.org,2002:timestamp"]
    for key, values in yaml.SafeLoader.yaml_implicit_resolvers.items()
}


def mapping(loader, node, deep=False):
    result = {}
    for key_node, value_node in node.value:
        key = loader.construct_object(key_node, deep=deep)
        if not isinstance(key, str) or key in result:
            raise MetadataError("frontmatter keys must be unique strings")
        result[key] = loader.construct_object(value_node, deep=deep)
    return result


Loader.add_constructor(yaml.resolver.BaseResolver.DEFAULT_MAPPING_TAG, mapping)


def split_document(text):
    """Return metadata and original body without reformatting either view."""
    lines = text.splitlines(keepends=True)
    if not lines or lines[0].strip() != "---":
        return None, text
    end = next((i for i in range(1, len(lines)) if lines[i].strip() == "---"), None)
    if end is None:
        raise MetadataError("frontmatter fence was never closed")
    raw = "".join(lines[1:end])
    try:
        if any(isinstance(event, yaml.AliasEvent) for event in yaml.parse(raw)):
            raise MetadataError("YAML aliases are not supported in Honua's publishing profile")
        fields = yaml.load(raw, Loader=Loader)
    except yaml.YAMLError as exc:
        raise MetadataError(f"invalid YAML frontmatter: {exc}") from exc
    if fields is None:
        fields = {}
    if not isinstance(fields, dict):
        raise MetadataError("frontmatter must be a mapping")
    return fields, "".join(lines[end + 1:])


def parse_frontmatter(text):
    try:
        fields, _ = split_document(text)
        return fields, None
    except MetadataError as exc:
        return {}, str(exc)
