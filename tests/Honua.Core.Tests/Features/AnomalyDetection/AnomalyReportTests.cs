// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.AnomalyDetection.Domain;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Core.Tests.Features.AnomalyDetection;

/// <summary>
/// Unit tests for anomaly detection domain models and report structures.
/// </summary>
[Protocol(Protocols.TestQuality)]
public sealed class AnomalyReportTests
{
    [UnitTest]
    [Operation(Operations.Query)]
    public void AnomalyReport_NoAnomalies_HasAnomaliesIsFalse()
    {
        var report = new AnomalyReport
        {
            LayerName = "test_layer",
            FeaturesScanned = 100,
        };

        report.HasAnomalies.Should().BeFalse();
        report.TotalAnomalyCount.Should().Be(0);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void AnomalyReport_WithGeometryAnomaly_HasAnomaliesIsTrue()
    {
        var report = new AnomalyReport
        {
            LayerName = "test_layer",
            FeaturesScanned = 100,
            GeometryAnomalies =
            [
                new GeometryAnomaly
                {
                    Type = GeometryAnomalyType.InvalidGeometry,
                    Reason = "5 features have self-intersecting geometry",
                    Severity = AnomalySeverity.Error,
                    AffectedCount = 5,
                    SampleFeatureIds = [1, 2, 3],
                }
            ],
        };

        report.HasAnomalies.Should().BeTrue();
        report.TotalAnomalyCount.Should().Be(1);
        report.GeometryAnomalies[0].SampleFeatureIds.Should().HaveCount(3);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void AnomalyReport_WithAttributeAnomaly_HasAnomaliesIsTrue()
    {
        var report = new AnomalyReport
        {
            LayerName = "test_layer",
            FeaturesScanned = 100,
            AttributeAnomalies =
            [
                new AttributeAnomaly
                {
                    Type = AttributeAnomalyType.NullCluster,
                    FieldName = "description",
                    Reason = "80% of values are null",
                    Severity = AnomalySeverity.Warning,
                    AffectedCount = 80,
                }
            ],
        };

        report.HasAnomalies.Should().BeTrue();
        report.TotalAnomalyCount.Should().Be(1);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void AnomalyReport_MixedAnomalies_TotalCountSumsAll()
    {
        var report = new AnomalyReport
        {
            LayerName = "test_layer",
            FeaturesScanned = 1000,
            GeometryAnomalies =
            [
                new GeometryAnomaly
                {
                    Type = GeometryAnomalyType.InvalidGeometry,
                    Reason = "3 invalid",
                    Severity = AnomalySeverity.Error,
                    AffectedCount = 3,
                },
                new GeometryAnomaly
                {
                    Type = GeometryAnomalyType.EmptyGeometry,
                    Reason = "2 empty",
                    Severity = AnomalySeverity.Warning,
                    AffectedCount = 2,
                },
            ],
            AttributeAnomalies =
            [
                new AttributeAnomaly
                {
                    Type = AttributeAnomalyType.NumericOutlier,
                    FieldName = "population",
                    Reason = "Outlier detected",
                    Severity = AnomalySeverity.Warning,
                    AffectedCount = 1,
                },
            ],
        };

        report.HasAnomalies.Should().BeTrue();
        report.TotalAnomalyCount.Should().Be(3);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void AnomalyAnalysisRequest_FieldDescriptor_RoundTrips()
    {
        var request = new AnomalyAnalysisRequest(
            TableName: "parcels",
            LayerName: "Land Parcels",
            GeometryColumn: "shape",
            DeclaredSrid: 4326,
            AttributeColumns:
            [
                new AnomalyFieldDescriptor("population", AnomalyFieldDataType.Numeric),
                new AnomalyFieldDescriptor("name", AnomalyFieldDataType.Text),
                new AnomalyFieldDescriptor("created_at", AnomalyFieldDataType.Temporal),
            ]);

        request.TableName.Should().Be("parcels");
        request.GeometryColumn.Should().Be("shape");
        request.DeclaredSrid.Should().Be(4326);
        request.AttributeColumns.Should().HaveCount(3);
        request.ObjectIdColumn.Should().Be("objectid");
        request.MaxSampleFeatures.Should().Be(5);
        request.ScanLimit.Should().Be(10000);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void GeometryAnomalyType_AllValuesAreDefined()
    {
        var values = Enum.GetValues<GeometryAnomalyType>();
        values.Should().HaveCount(5);
        values.Should().Contain(GeometryAnomalyType.InvalidGeometry);
        values.Should().Contain(GeometryAnomalyType.EmptyGeometry);
        values.Should().Contain(GeometryAnomalyType.SuspiciousAreaPerimeterRatio);
        values.Should().Contain(GeometryAnomalyType.SridMismatch);
        values.Should().Contain(GeometryAnomalyType.DuplicateVertices);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void AttributeAnomalyType_AllValuesAreDefined()
    {
        var values = Enum.GetValues<AttributeAnomalyType>();
        values.Should().HaveCount(3);
        values.Should().Contain(AttributeAnomalyType.NullCluster);
        values.Should().Contain(AttributeAnomalyType.HighCardinality);
        values.Should().Contain(AttributeAnomalyType.NumericOutlier);
    }

    [UnitTest]
    [Operation(Operations.Query)]
    public void AnomalySeverity_HasExpectedValues()
    {
        var values = Enum.GetValues<AnomalySeverity>();
        values.Should().HaveCount(3);
        values.Should().Contain(AnomalySeverity.Info);
        values.Should().Contain(AnomalySeverity.Warning);
        values.Should().Contain(AnomalySeverity.Error);
    }
}
