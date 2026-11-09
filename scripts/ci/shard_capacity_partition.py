"""Shared conservation checks for capacity splits."""

from types import ModuleType
from typing import NamedTuple


class PartitionSource(NamedTuple):
    coverage: ModuleType
    classes: dict


def current_source_paths(paths):
    relocated = []
    for path in paths:
        if path.startswith("src/Honua.Core/") and not path.startswith(
                ("src/Honua.Core/Core/", "src/Honua.Core/Abstractions/")):
            path = path.replace("src/Honua.Core/", "src/Honua.Core/Core/", 1)
        elif path.startswith("src/Honua.Protocols."):
            path = path.replace("src/Honua.Protocols.", "src/Honua.Protocols/", 1)
        relocated.append(path)
    return relocated


def check_method_partition(source, original, children):
    coverage, classes = source
    original = {**original, "paths": current_source_paths(original["paths"])}

    methods = coverage.selection_pool(classes, original["csproj"] or coverage.DEFAULT_CSPROJ)
    parent = coverage._FilterParser(original["filter"]).parse()
    expected = {method for method in methods if coverage._eval(parent, method)}
    parsed = [coverage._FilterParser(child["filter"]).parse() for child in children]
    selected = [{method for method in methods if coverage._eval(expression, method)} for expression in parsed]
    assert all(selected), "Empty child."
    assert set.union(*selected) == expected, "Lost original or leaked foreign methods."
    assert sum(map(len, selected)) == len(expected), "Duplicate method ownership."
    for child in children:
        for field in ("paths", "csproj", "test_timeout_minutes", "timeout_minutes", "max_cpu_count",
                      "upload_operator_eval_report", "upload_odata_evidence"):
            assert child[field] == original[field], f"Original {field} changed."
    return selected


def check_partition(source, original, children):
    selected = check_method_partition(source, original, children)
    coverage, classes = source
    parent = coverage._FilterParser(original["filter"]).parse()
    parsed = [coverage._FilterParser(child["filter"]).parse() for child in children]
    future_method = "FutureMethod_" + min(selected[0]).rsplit(".", 2)[-2]
    for entry in classes.values():
        if entry["csproj"] != original["csproj"]:
            continue
        fqn = entry["fqn"]
        owners = [coverage._eval(expression, fqn) for expression in parsed]
        assert sum(owners) == int(coverage._eval(parent, fqn)), f"Class ownership changed: {fqn}"
        for method in entry["methods"] | {future_method}:
            name = fqn + "." + method
            expected_owners = owners if coverage._eval(parent, name) else [False] * len(parsed)
            assert [coverage._eval(expression, name) for expression in parsed] == expected_owners, name
    return selected
