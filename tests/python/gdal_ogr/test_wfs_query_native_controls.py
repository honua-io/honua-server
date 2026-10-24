# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.
"""Real native-client controls; generic runs retain the existing GDAL prerequisite."""
from types import SimpleNamespace

import httpx
import pytest

from .conftest import OgrResult
from .controlled_wfs import source_document
from .test_wfs_export import TestWfsExport as WfsExports
from .test_wfs_query import TestWfsQuery as WfsQueries

pytestmark = pytest.mark.gdal


@pytest.fixture(scope="session", autouse=True)
def _write_evidence_report():
    """Controlled service observations never enter a live qualification report."""
    yield


@pytest.mark.parametrize("operation,fault", [("attribute", None), ("bbox", None), ("empty", None), ("csv", None),
                                             ("attribute", "ignored-where"), ("bbox", "ignored-spat")])
def test_real_gdal_wfs_client_exercises_the_changed_probe_and_ignored_argument_controls(monkeypatch, tmp_path, operation, fault):
    import shutil
    import subprocess

    from .controlled_wfs import controlled_wfs

    assert shutil.which("ogr2ogr"), "real GDAL is required for this selected client control"
    monkeypatch.setenv("HONUA_BASE_URL", "http://controlled")
    document = source_document()
    collector = SimpleNamespace(records=[])
    collector.record = lambda *args, **kwargs: collector.records.append((args, kwargs))
    with controlled_wfs(document) as (url, requests):
        monkeypatch.setenv("HONUA_BASE_URL", url)
        def ogr(args):
            args = list(args)
            option, count = ("-where", 2) if fault == "ignored-where" else ("-spat", 5)
            if fault and option in args:
                index = args.index(option)
                del args[index:index + count]
            result = subprocess.run(args, capture_output=True, text=True, timeout=30, check=False)
            return OgrResult(args, result.returncode, result.stdout, result.stderr)
        dsn = f"WFS:{url}/wfs?SERVICE=WFS&VERSION=2.0.0&REQUEST=GetCapabilities"
        with httpx.Client(base_url=url, trust_env=False) as client:
            def execute():
                if operation == "attribute":
                    WfsQueries().test_attribute_query(client, dsn, ogr, "fixture:points", collector)
                elif operation == "bbox":
                    WfsQueries().test_spatial_query_bbox(client, dsn, ogr, "fixture:points", collector)
                elif operation == "csv":
                    WfsExports().test_export_csv(client, dsn, ogr, "fixture:points", tmp_path, collector)
                else:
                    WfsQueries().test_spatial_query_empty_bbox(dsn, ogr, "fixture:points")
            if fault:
                with pytest.raises(AssertionError, match="exact feature count"):
                    execute()
                assert collector.records == []
            else:
                execute()
        assert any(request["operation"].lower() == "describefeaturetype" for request in requests), "native WFS schema path was not exercised"
        assert any(request["operation"].lower() == "getfeature" for request in requests)
