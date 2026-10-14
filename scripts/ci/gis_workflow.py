"""Static checks for Honua's descriptive GIS workflow profile.

This checks typed composition and declared effects, not runtime authorization,
successful execution, or the truth of free-text preconditions and outcomes.
"""
from __future__ import annotations

import re


class WorkflowError(ValueError):
    pass


NAME = re.compile(r"[a-z][a-z0-9_-]*\Z")
REFERENCE = re.compile(r"\$(inputs\.([a-z][a-z0-9_-]*)|actions\.([a-z][a-z0-9_-]*)\.([a-z][a-z0-9_-]*))\Z")
EFFECTS = {"read", "create", "update", "execute", "propose", "observe", "delete"}
TARGETS = {"source_data", "derived_artifact", "map_content", "catalog_configuration",
           "access_policy", "deployment", "job_state", "observation", "workspace_content"}


def require(condition, message):
    if not condition:
        raise WorkflowError(message)


def text(value, label):
    require(isinstance(value, str) and bool(value.strip()), f"{label} must be a nonempty string")


def texts(value, label, *, allow_empty=False):
    require(isinstance(value, list) and (allow_empty or value), f"{label} must be a list")
    for item in value:
        text(item, label)


def concept_id(value):
    text(value, "concept")
    require(value.startswith("/") and value.endswith(".md") and "\\" not in value and
            all(part not in ("", ".", "..") for part in value[1:].split("/")), "concept must be a portable bundle-root Markdown path")
    return value[1:-3]


def slots(values, known, label, *, binding=False):
    require(isinstance(values, list), f"{label} must be a list")
    names = {}
    for value in values:
        require(isinstance(value, dict), f"{label} slot must be an object")
        name = value.get("name")
        require(isinstance(name, str) and NAME.fullmatch(name) and name not in names, f"{label}: invalid or duplicate slot name")
        cid = concept_id(value.get("concept"))
        require(cid in known, f"{label}: unknown or unpublished concept {cid}")
        if "required" in value:
            require(type(value["required"]) is bool, f"{label}: required must be boolean")
        if binding:
            require(isinstance(value.get("from"), str) and REFERENCE.fullmatch(value["from"]), f"{label}: invalid slot reference")
        names[name] = cid
    return names


def validate(workflow, known_concepts):
    require(isinstance(workflow, dict) and workflow.get("profile") == "honua.gis-workflow/v1", "unknown workflow profile")
    text(workflow.get("intent"), "intent")
    inputs = slots(workflow.get("inputs"), known_concepts, "inputs")
    slots(workflow.get("outputs"), known_concepts, "outputs", binding=True)
    texts(workflow.get("constraints"), "constraints")
    ambiguities = workflow.get("ambiguities", [])
    require(isinstance(ambiguities, list), "ambiguities must be a list")
    for item in ambiguities:
        require(isinstance(item, dict), "ambiguity must be an object")
        text(item.get("phrase"), "ambiguous phrase")
        texts(item.get("interpretations"), "interpretations")
        require(len(item["interpretations"]) >= 2, "ambiguity needs multiple possible meanings")
        text(item.get("clarify"), "clarification")
    actions = workflow.get("actions")
    require(isinstance(actions, list) and actions, "workflow needs actions")
    by_id, output_types, dependencies = {}, {}, {}
    for action in actions:
        require(isinstance(action, dict), "action must be an object")
        aid = action.get("id")
        require(isinstance(aid, str) and NAME.fullmatch(aid) and aid not in by_id, "invalid or duplicate action ID")
        by_id[aid] = action
        text(action.get("verb"), f"{aid}.verb")
        require(action.get("effect") in EFFECTS, f"{aid}: unknown effect")
        require(action.get("effectTarget") in TARGETS, f"{aid}: unknown effect target")
        # ADR-0028: an approval annotation, human-facing API, or rollback intent
        # does not turn source-data mutation into an AI workflow.
        require(action["effectTarget"] != "source_data" or action["effect"] == "read", f"{aid}: ADR-0028 forbids AI source-data mutation")
        for field in ("preconditions", "authorization", "checks", "recovery"):
            texts(action.get(field), f"{aid}.{field}")
        text(action.get("idempotency"), f"{aid}.idempotency")
        slots(action.get("inputs"), known_concepts, f"{aid}.inputs", binding=True)
        output_types[aid] = slots(action.get("outputs"), known_concepts, f"{aid}.outputs")
        dependencies[aid] = set()
        if action["effect"] in {"create", "update", "delete", "execute"} and action["effectTarget"] in {
            "map_content", "workspace_content", "catalog_configuration", "access_policy", "deployment"
        }:
            require(isinstance(action.get("rollback"), dict), f"{aid}: changed state needs a recovery contract")
        if "rollback" in action:
            rollback = action["rollback"]
            require(isinstance(rollback, dict), f"{aid}: invalid rollback contract")
            for field in ("snapshot", "revisionGuard", "scope", "compensation"):
                text(rollback.get(field), f"{aid}.rollback.{field}")
            texts(rollback.get("verification"), f"{aid}.rollback.verification")
            texts(rollback.get("nonReversible"), f"{aid}.rollback.nonReversible", allow_empty=True)
        interfaces = action.get("interfaces", [])
        require(isinstance(interfaces, list), f"{aid}: interfaces must be a list")
        for interface in interfaces:
            require(isinstance(interface, dict) and interface.get("kind") in {"http", "mcp", "cli", "spec", "sdk"}, f"{aid}: unknown interface kind")
            for field in ("operation", "contract"):
                text(interface.get(field), f"{aid}.interface.{field}")
            if interface["kind"] == "http":
                require(interface.get("method") in {"GET", "HEAD", "POST", "PUT", "PATCH", "DELETE"}, f"{aid}: HTTP method required")
                require(isinstance(interface.get("path"), str) and interface["path"].startswith("/"), f"{aid}: HTTP path required")

    def resolve(binding, owner):
        match = REFERENCE.fullmatch(binding["from"])
        if match.group(2):
            actual = inputs.get(match.group(2))
        else:
            other, name = match.group(3), match.group(4)
            actual = output_types.get(other, {}).get(name)
            if owner is not None:
                dependencies[owner].add(other)
        require(actual is not None, f"{owner or 'output'}: undeclared reference {binding['from']}")
        require(actual == concept_id(binding["concept"]), f"{owner or 'output'}: concept type mismatch at {binding['from']}")

    for aid, action in by_id.items():
        for binding in action["inputs"]:
            resolve(binding, aid)
    for binding in workflow["outputs"]:
        resolve(binding, None)
    visited, active, order = set(), set(), []
    def visit(aid):
        require(aid not in active, "workflow dependency cycle")
        if aid in visited:
            return
        active.add(aid)
        for dependency in sorted(dependencies[aid]):
            visit(dependency)
        active.remove(aid)
        visited.add(aid)
        order.append(aid)
    for aid in by_id:
        visit(aid)
    return {"actions": len(actions), "dependencyOrder": order, "executionVerified": False}
