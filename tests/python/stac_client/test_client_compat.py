# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
Canonical STAC client compatibility proof using PySTAC and PySTAC-Client.
"""

from __future__ import annotations

import pystac
import pytest
from pystac_client import Client

from .conftest import CompatibilityEvidenceCollector


@pytest.mark.integration
@pytest.mark.stac
class TestStacClientCompatibility:
    """Exercise the live STAC API surface with canonical Python tooling."""

    def test_pystac_validates_catalog_document(
        self,
        stac_http_client,
        evidence_collector: CompatibilityEvidenceCollector,
    ):
        """PySTAC parses and validates the STAC landing page."""
        response = stac_http_client.get("/stac")
        assert response.status_code == 200

        document = response.json()
        catalog = pystac.Catalog.from_dict(document, preserve_dict=False)
        catalog.validate()

        assert catalog.id == "honua-stac-catalog"
        evidence_collector.record(
            "test_pystac_validates_catalog_document",
            "PySTAC validated the raw /stac catalog document",
            "pass",
            detail=f"catalog_id={catalog.id}",
        )

    def test_pystac_validates_collection_and_item_documents(
        self,
        stac_http_client,
        test_collection_id: str,
        evidence_collector: CompatibilityEvidenceCollector,
    ):
        """PySTAC validates raw STAC collection and item documents."""
        collection_response = stac_http_client.get(f"/stac/collections/{test_collection_id}")
        assert collection_response.status_code == 200
        collection = pystac.Collection.from_dict(
            collection_response.json(),
            preserve_dict=False,
        )
        collection.validate()

        items_response = stac_http_client.get(
            f"/stac/collections/{test_collection_id}/items",
            params={"limit": 1},
        )
        assert items_response.status_code == 200

        features = items_response.json()["features"]
        assert features

        item = pystac.Item.from_dict(features[0], preserve_dict=False)
        item.validate()

        assert item.collection_id == test_collection_id
        evidence_collector.record(
            "test_pystac_validates_collection_and_item_documents",
            "PySTAC validated raw collection and item payloads",
            "pass",
            detail=f"collection_id={collection.id} item_id={item.id}",
        )

    def test_pystac_client_discovers_seeded_collection(
        self,
        stac_api_url: str,
        test_collection_id: str,
        evidence_collector: CompatibilityEvidenceCollector,
    ):
        """PySTAC-Client can discover the seeded collection from the live API."""
        client = Client.open(stac_api_url)
        collections = list(client.get_collections())

        assert any(collection.id == test_collection_id for collection in collections)

        discovered = client.get_collection(test_collection_id)
        assert discovered is not None
        assert discovered.id == test_collection_id

        evidence_collector.record(
            "test_pystac_client_discovers_seeded_collection",
            "PySTAC-Client discovered the seeded collection through /stac",
            "pass",
            detail=f"collection_count={len(collections)} collection_id={discovered.id}",
        )

    def test_pystac_client_search_paginates_filtered_results(
        self,
        stac_api_url: str,
        test_collection_id: str,
        evidence_collector: CompatibilityEvidenceCollector,
    ):
        """PySTAC-Client search follows paging links and preserves bbox filtering."""
        client = Client.open(stac_api_url)
        search = client.search(
            collections=[test_collection_id],
            bbox=[-122.50, 37.70, -122.44, 37.74],
            limit=2,
            max_items=4,
        )

        items = list(search.items())
        names = {item.properties["name"] for item in items}

        assert len(items) == 4
        assert names == {"alpha", "beta", "gamma", "delta"}
        assert {item.collection_id for item in items} == {test_collection_id}

        evidence_collector.record(
            "test_pystac_client_search_paginates_filtered_results",
            "PySTAC-Client paged a bbox-filtered STAC search without dropping matches",
            "pass",
            detail=f"returned={len(items)} names={','.join(sorted(names))}",
        )
