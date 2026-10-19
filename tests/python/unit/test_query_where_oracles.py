"""Controlled HTTP negatives call the actual positive FeatureServer probe methods."""
from copy import deepcopy

import httpx
import pytest
from feature_server import test_query as probe


@pytest.mark.parametrize("method", list(probe.WHERE_CASES))
@pytest.mark.parametrize("mutation", ["positive", "over-return", "empty", "body-error", "wrong-value", "duplicate", "missing-id", "wrong-id", "boolean-count"])
def test_where_probe_requires_exact_independent_rows(method, mutation):
    cohort = {key: {"objectid": 101 + index, **attributes}
              for index, (key, attributes) in enumerate(probe.WHERE_ROWS.items())}
    predicate, expected_keys = probe.WHERE_CASES[method]
    rows = [deepcopy(cohort[key]) for key in expected_keys]
    if mutation == "over-return":
        rows = list(cohort.values())
    elif mutation == "empty":
        rows = []
    elif mutation == "wrong-value":
        rows[0]["count"] += 1
    elif mutation == "duplicate":
        rows.append(deepcopy(rows[0]))
    elif mutation == "wrong-id":
        rows[0]["objectid"] += 1000
    elif mutation == "boolean-count":
        rows[0]["count"] = True
    elif mutation == "missing-id":
        del rows[0]["objectid"]
    body = {"features": [{"attributes": row} for row in rows]}
    if mutation == "body-error":
        # Even an otherwise exact feature array cannot conceal a body error.
        body["error"] = {"code": 400, "message": "query refused"}

    def respond(request):
        assert request.url.path == "/rest/services/worker/FeatureServer/1000/query"
        assert request.url.params["where"] == "objectid IN (101,102,103,104,105,106) AND (" + predicate + ")"
        assert request.url.params["outFields"] == "objectid,name,count,description"
        return httpx.Response(200, json=body)

    with httpx.Client(base_url="http://oracle.invalid", transport=httpx.MockTransport(respond)) as client:
        run = getattr(probe.TestQueryWhereClause(), method)
        if mutation == "positive":
            run(client, "worker", 1000, cohort)
        else:
            with pytest.raises(AssertionError):
                run(client, "worker", 1000, cohort)
