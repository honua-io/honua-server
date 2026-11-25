# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

"""OGC API Records interoperability with OWSLib."""

from urllib.parse import urljoin

import pytest
from owslib.ogcapi.records import Records
from owslib.util import http_get

CATALOG_ID = "honua-catalog"


@pytest.fixture(scope="session")
def records_client(base_url):
    return Records(f"{base_url}/ogc/records")


@pytest.mark.cert("CERT-DISC-01")
def test_lists_catalog(records_client, records_collector, timer):
    catalogs = records_client.records()

    assert CATALOG_ID in catalogs
    records_collector.record(
        "CERT-DISC-01", "pass", duration_ms=timer.ms, measured_count=len(catalogs),
    )


@pytest.mark.cert("CERT-DISC-02")
def test_reads_catalog_metadata(records_client, records_collector, timer):
    catalog = records_client.collection(CATALOG_ID)

    assert catalog["id"] == CATALOG_ID
    assert catalog["title"]
    assert any(link["rel"] == "items" for link in catalog["links"])
    records_collector.record("CERT-DISC-02", "pass", duration_ms=timer.ms)


@pytest.mark.cert("CERT-PAGE-01")
def test_reads_first_page_and_record(records_client, records_collector, timer):
    page = records_client.collection_items(CATALOG_ID, limit=1)
    [entry] = page["features"]
    record = records_client.collection_item(CATALOG_ID, entry["id"])

    assert page["numberReturned"] == 1
    assert record["id"] == entry["id"]
    assert record["properties"]["title"] == entry["properties"]["title"]
    assert record["properties"]["title"]
    records_collector.record(
        "CERT-PAGE-01", "pass", duration_ms=timer.ms, measured_count=len(page["features"]),
    )


@pytest.mark.cert("CERT-PAGE-02")
def test_follows_next_page_link(base_url, records_client, records_collector, timer):
    first = records_client.collection_items(CATALOG_ID, limit=1)
    [next_link] = [link["href"] for link in first["links"] if link["rel"] == "next"]
    items_url = f"{base_url}/ogc/records/collections/{CATALOG_ID}/items"
    response = http_get(urljoin(items_url, next_link), headers=records_client.headers, timeout=30)
    response.raise_for_status()
    second = response.json()
    expected = records_client.collection_items(CATALOG_ID, limit=2)

    assert len(second["features"]) == 1
    paged_ids = [feature["id"] for feature in first["features"] + second["features"]]
    assert len(set(paged_ids)) == 2
    assert paged_ids == [feature["id"] for feature in expected["features"]]
    records_collector.record("CERT-PAGE-02", "pass", duration_ms=timer.ms)
