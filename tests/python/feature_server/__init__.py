# Copyright (c) Honua. All rights reserved.
# Licensed under the Elastic License 2.0. See LICENSE in the project root.

"""
GeoServices REST/FeatureServer integration test suite.

Tests Esri GeoServices REST API compliance with:
- Service and layer metadata endpoints
- Query operations (GET/POST) with SQL-like filtering
- ApplyEdits for feature CRUD operations
- QueryRelatedRecords for relationship traversal
- Attachment management (add, update, delete, query)
- MVT/Vector tiles
"""
