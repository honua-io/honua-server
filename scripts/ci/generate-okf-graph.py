#!/usr/bin/env python3
"""Derive an agent-readable graph from the same OKF files people read.

Never rewrites human documents. Excluded evidence, internal notes and private
test answers are not indexed. A compact context is a projection, not a second
editable knowledge base and not a runtime permission grant.
"""
from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import re
from urllib.parse import unquote, urlsplit

from gis_workflow import WorkflowError, concept_id, validate as validate_workflow
from okf_frontmatter import MetadataError, split_document


REPO = Path(__file__).resolve().parents[2]
DEFAULT_OUTPUT = REPO / "docs/okf/domain-graph.v1.json"
LINK = re.compile(r"\[[^\]]*\]\(([^\s)]+)(?:\s+[^)]*)?\)")
REF_LINK = re.compile(r"^\s*\[[^\]]+\]:\s*(\S+)", re.MULTILINE)
FENCES = re.compile(r"^\s*(`{3,}|~{3,})")


def digest(data):
    return "sha256:" + hashlib.sha256(data).hexdigest()


def canonical(value):
    return json.dumps(value, sort_keys=True, ensure_ascii=False, allow_nan=False, separators=(",", ":")).encode()


def body_links(body):
    visible, fence = [], None
    for line in body.splitlines():
        match = FENCES.match(line)
        if match:
            marker = match.group(1)
            if fence is None:
                fence = marker
            elif marker[0] == fence[0] and len(marker) >= len(fence):
                fence = None
            continue
        if fence is None:
            visible.append(line)
    text = "\n".join(visible)
    return LINK.findall(text) + REF_LINK.findall(text)


def target_id(root, document, reference):
    parsed = urlsplit(reference)
    if parsed.scheme or parsed.netloc or not parsed.path:
        return None
    path = unquote(parsed.path)
    target = (root / path.lstrip("/")) if path.startswith("/") else document.parent / path
    resolved = target.resolve()
    if not resolved.is_relative_to(root.resolve()) or resolved.suffix != ".md":
        return None
    return resolved.relative_to(root.resolve()).with_suffix("").as_posix()


def build(repo=REPO):
    manifest_path = repo / "scripts/ci/okf-bundle.v1.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    root = repo / manifest["root"]
    included = {(repo / e["path"]).resolve() for e in manifest.get("includedFiles", [])}
    excluded_files = {(repo / e["path"]).resolve() for e in manifest["excludedFiles"]}
    excluded_dirs = [(repo / e["path"]).resolve() for e in manifest["excludedDirs"]]
    nodes, documents = {}, {}
    for path in sorted(root.rglob("*.md")):
        resolved = path.resolve()
        if not resolved.is_relative_to(root.resolve()) or path.is_symlink():
            raise MetadataError("bundle source escapes root or is a symlink")
        if resolved not in included and (resolved in excluded_files or any(p in resolved.parents for p in excluded_dirs)):
            continue
        text = path.read_text(encoding="utf-8")
        metadata, body = split_document(text)
        if not metadata or not isinstance(metadata.get("type"), str) or not metadata["type"].strip():
            raise MetadataError(f"untyped concept: {path.relative_to(root)}")
        cid = path.relative_to(root).with_suffix("").as_posix()
        nodes[cid] = {"id": cid, "path": path.relative_to(root).as_posix(),
                      "documentDigest": digest(text.encode()), "metadata": metadata,
                      "humanDocument": f"https://github.com/honua-io/honua-server/blob/trunk/docs/{path.relative_to(root).as_posix()}"}
        documents[cid] = (path, body)
    edges, unresolved, workflows, bindings = set(), set(), {}, []
    known = set(nodes)
    for cid, node in nodes.items():
        path, body = documents[cid]
        metadata = node["metadata"]
        for reference in body_links(body):
            target = target_id(root, path, reference)
            if target is None:
                continue
            if target in nodes:
                edges.add((cid, "links_to", target))
            else:
                # OKF permits missing links. Retain their identity without
                # crawling private/excluded documents to fill in the gap.
                unresolved.add((cid, target))
        relations = metadata.get("relations", [])
        if not isinstance(relations, list):
            raise WorkflowError(f"{cid}: relations must be a list")
        for relation in relations:
            if not isinstance(relation, dict) or not isinstance(relation.get("predicate"), str) or not relation["predicate"].strip():
                raise WorkflowError(f"{cid}: invalid semantic relation")
            target = concept_id(relation.get("target"))
            if target not in known:
                raise WorkflowError(f"{cid}: relation names an unpublished concept: {target}")
            edges.add((cid, relation["predicate"], target))
        if "workflow" in metadata:
            workflow = metadata["workflow"]
            workflows[cid] = validate_workflow(workflow, known)
            for action in workflow["actions"]:
                for slot in action["inputs"]:
                    edges.add((cid, "uses", concept_id(slot["concept"])))
                for slot in action["outputs"]:
                    edges.add((cid, "produces", concept_id(slot["concept"])))
                for interface in action.get("interfaces", []):
                    binding = {"workflow": cid, "action": action["id"],
                               "operation": interface["operation"], "kind": interface["kind"],
                               "validation": "documentation_reference_only",
                               "runtimeDiscoveryRequired": True, "executionVerified": False}
                    target = target_id(root, path, interface["contract"])
                    if target is not None and target not in known:
                        raise WorkflowError(f"{cid}: interface contract is not published: {interface['contract']}")
                    if target in known:
                        edges.add((cid, "interface_contract", target))
                    # A local OpenAPI binding is verified against its actual
                    # operation. Markdown/MCP/CLI links still require live
                    # discovery; a documentation link isn't schema validation.
                    contract = urlsplit(interface["contract"])
                    if interface["kind"] == "http" and not contract.scheme and contract.path.endswith(".json"):
                        spec_path = (root / contract.path.lstrip("/")) if contract.path.startswith("/") else path.parent / contract.path
                        if not spec_path.resolve().is_relative_to(root.resolve()):
                            raise WorkflowError("OpenAPI contract escapes bundle")
                        spec = json.loads(spec_path.read_text(encoding="utf-8"))
                        if interface["method"].lower() not in spec.get("paths", {}).get(interface["path"], {}):
                            raise WorkflowError(f"{cid}: HTTP operation absent from linked OpenAPI")
                        binding["validation"] = "openapi_method_and_path"
                        binding["contractDigest"] = digest(spec_path.read_text(encoding="utf-8").encode())
                    bindings.append(binding)
    ordered_nodes = [nodes[key] for key in sorted(nodes)]
    commitment = {"manifestDigest": digest(manifest_path.read_text(encoding="utf-8").encode()),
                  "documents": [{"id": n["id"], "digest": n["documentDigest"]} for n in ordered_nodes],
                  "interfaceBindings": bindings}
    return {"schemaVersion": 1, "profile": "honua.okf-agent-graph/v1", "okfVersion": manifest["spec"]["version"],
            "sourceRepository": "https://github.com/honua-io/honua-server", "contentNormalization": "UTF-8/LF",
            "bundleDigest": digest(canonical(commitment)), "nodes": ordered_nodes,
            "edges": [{"source": a, "relation": r, "target": b} for a, r, b in sorted(edges)],
            "unresolvedLinks": [{"source": a, "target": b} for a, b in sorted(unresolved)],
            "workflows": workflows, "interfaceBindings": bindings, "executionVerified": False}


def context(graph, query, limit=8):
    """Retrieve lexical matches plus direct semantic neighbors, preserving ambiguity."""
    words = set(re.findall(r"\w+", query.casefold()))
    ranked = []
    for node in graph["nodes"]:
        fields = node["metadata"]
        labels = [fields.get("title", ""), *fields.get("aliases", [])]
        score = sum(3 * len(words & set(re.findall(r"\w+", label.casefold()))) for label in labels)
        score += len(words & set(re.findall(r"\w+", fields.get("description", "").casefold())))
        if score:
            ranked.append((-score, node["id"]))
    ids = [cid for _, cid in sorted(ranked)[:limit]]
    direct = [edge["target"] for edge in graph["edges"] if edge["source"] in ids and edge["relation"] != "links_to"]
    selected = list(dict.fromkeys(ids + direct))[:limit * 2]
    return {"schemaVersion": 1, "bundleDigest": graph["bundleDigest"], "query": query,
            "selection": "Lexical matches and direct semantic neighbors; resolve ambiguity using full human documents and live interface discovery.",
            "nodes": [node for node in graph["nodes"] if node["id"] in selected],
            "edges": [edge for edge in graph["edges"] if edge["source"] in selected and edge["target"] in selected],
            "executionAuthorized": False}


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--output", type=Path, default=DEFAULT_OUTPUT)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--query")
    args = parser.parse_args(argv)
    try:
        graph = build()
        value = context(graph, args.query) if args.query is not None else graph
        encoded = json.dumps(value, indent=2, ensure_ascii=False, allow_nan=False) + "\n"
        if args.query is not None:
            print(encoded, end="")
        elif args.check:
            if not args.output.is_file() or args.output.read_text(encoding="utf-8") != encoded:
                raise WorkflowError("agent graph is stale; regenerate from the OKF sources")
        else:
            args.output.parent.mkdir(parents=True, exist_ok=True)
            args.output.write_text(encoded, encoding="utf-8", newline="\n")
        return 0
    except (MetadataError, WorkflowError, OSError, ValueError, KeyError, TypeError) as exc:
        print(f"OKF graph refused: {exc}")
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
