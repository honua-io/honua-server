# Copyright (c) Honua Geo Inc. All rights reserved.
# Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

from __future__ import annotations

import json

import pytest

from shared.postgis import PostGISFixture


@pytest.mark.parametrize("geometry_field", ["shape", "geometry"])
def test_seed_metadata_snapshot_keeps_logical_geometry_reference_separate_from_storage(geometry_field):
    connection = SnapshotConnection(geometry_field)

    PostGISFixture()._seed_metadata_v2_snapshot(connection)

    assert set(connection.documents) == {"default", "Test", "Development"}
    for graph in connection.documents.values():
        resource = next(resource for resource in graph["resources"] if resource["type"] == "feature-dataset")
        declared_fields = {field["name"]: field for field in resource["schemaFields"]}
        primary_geometry = resource["spatial"]["primaryGeometryField"]
        assert primary_geometry in declared_fields, "serving guards require every logical field reference to be declared"
        assert primary_geometry == geometry_field
        assert declared_fields[primary_geometry]["type"] == "geometry"
        assert declared_fields[resource["temporal"]["startTimeField"]]["type"] == "datetime"
        binding = next(binding for binding in graph["storageBindings"]
                       if binding["metadata"]["id"] == resource["primaryStorageBindingId"])
        assert binding["options"]["geometryColumn"] == "geometry"
        assert binding["storageLayerId"] == 741
        publication = next(publication for publication in graph["publications"]
                           if publication["publicationType"] == "stac-collection")
        assert publication["resourceId"] == resource["metadata"]["id"]
        assert publication["storageBindingId"] == binding["metadata"]["id"]


class SnapshotConnection:
    def __init__(self, geometry_field):
        self.geometry_field = geometry_field
        self.documents = {}

    def execute(self, sql, parameters=None):
        if "FROM honua.service_layers sl" in sql:
            return SnapshotRows([("test_service", "Test service", 741, "logical_layer", "Test layer",
                                  "public", "features", "Point", 4326, -180, -90, 180, 90, "created_at")])
        if "FROM honua.layer_fields" in sql:
            return SnapshotRows([(741, self.geometry_field, "Geometry", True, "Geometry"),
                                 (741, "created_at", "DateTime", True, "Observation time")])
        if "INSERT INTO honua.metadata_v2_snapshots" in sql:
            environment, document, _ = parameters
            self.documents[environment] = json.loads(document)
        return SnapshotRows([])


class SnapshotRows:
    def __init__(self, rows):
        self.rows = rows

    def fetchall(self):
        return self.rows
