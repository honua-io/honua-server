// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Server.Features.Admin;

namespace Honua.Server.Tests.Features.Admin;

public sealed class MigrationEvidenceGeneratorTests
{
    [Fact]
    public void CanonicalizeFeatureRows_WithGeoJsonPayload_UsesMappedFieldNamesBeforeCanonicalFallback()
    {
        using var sourcePayload = JsonDocument.Parse("""
            {
              "features": [
                {
                  "properties": {
                    "Parcel ID": 101,
                    "Owner Name": "Alpha"
                  }
                }
              ]
            }
            """);
        using var targetPayload = JsonDocument.Parse("""
            {
              "features": [
                {
                  "properties": {
                    "parcel_id": 101,
                    "owner_name": "Alpha"
                  }
                }
              ]
            }
            """);

        var fieldMappings = new MigrationEvidenceGenerator.FieldMappingSet(
            [
                new MigrationEvidenceGenerator.FieldMappingEntry("Parcel ID", "parcel_id", "parcelid", "esriFieldTypeInteger"),
                new MigrationEvidenceGenerator.FieldMappingEntry("Owner Name", "owner_name", "ownername", "esriFieldTypeString")
            ],
            StringField: null,
            NumericField: null,
            DateField: null);

        var sourceRows = MigrationEvidenceGenerator.CanonicalizeFeatureRows(
            sourcePayload.RootElement,
            fieldMappings,
            MigrationEvidenceGenerator.FeatureRowFieldOrigin.Source,
            geoJson: true);
        var targetRows = MigrationEvidenceGenerator.CanonicalizeFeatureRows(
            targetPayload.RootElement,
            fieldMappings,
            MigrationEvidenceGenerator.FeatureRowFieldOrigin.Target,
            geoJson: true);

        sourceRows.Should().Equal(["parcelid=101|ownername=Alpha"]);
        targetRows.Should().Equal(sourceRows);
    }
}
