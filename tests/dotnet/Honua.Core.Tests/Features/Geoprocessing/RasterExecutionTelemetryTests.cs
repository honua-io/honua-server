// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Raster;

namespace Honua.Core.Tests.Features.Geoprocessing;

public sealed class RasterExecutionTelemetryTests
{
    [Fact]
    public void PlanningMetricTags_UseOnlyBoundedCanonicalDimensions()
    {
        var tags = RasterExecutionTelemetry.CreatePlanningMetricTags(
            RasterEngine.GdalNative,
            RasterExecutionPlacement.RemoteBackend,
            RasterTelemetryAdmissionClass.Accepted,
            RasterTelemetryOutcome.Selected);

        var values = tags.ToDictionary(tag => tag.Key, tag => tag.Value);

        Assert.Equal(4, values.Count);
        Assert.Equal("gdal-native", values[RasterExecutionTelemetry.Dimensions.Engine]);
        Assert.Equal("remote-backend", values[RasterExecutionTelemetry.Dimensions.Placement]);
        Assert.Equal("accepted", values[RasterExecutionTelemetry.Dimensions.Admission]);
        Assert.Equal("selected", values[RasterExecutionTelemetry.Dimensions.Outcome]);
        Assert.All(values.Keys, key => Assert.True(RasterExecutionTelemetry.IsAllowedMetricDimension(key)));
    }

    [Fact]
    public void PlanningRefusalTags_DoNotCarryExactReasonOrIdentifiers()
    {
        var exactReason = "tenant-123/source=https://signed.example/object?token=secret";
        var admission = RasterExecutionTelemetry.ClassifyPlanningRefusal(exactReason, isRetryable: false);
        var tags = RasterExecutionTelemetry.CreatePlanningMetricTags(
            engine: null,
            placement: null,
            admission,
            RasterTelemetryOutcome.Refused);

        var values = tags.ToDictionary(tag => tag.Key, tag => tag.Value);

        Assert.Equal("none", values[RasterExecutionTelemetry.Dimensions.Engine]);
        Assert.Equal("none", values[RasterExecutionTelemetry.Dimensions.Placement]);
        Assert.Equal("unknown", values[RasterExecutionTelemetry.Dimensions.Admission]);
        Assert.DoesNotContain(values, pair => string.Equals(pair.Key, "reason", StringComparison.Ordinal));
        Assert.DoesNotContain(values.Values, value => string.Equals(value?.ToString(), exactReason, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("no-eligible-raster-placement", false, RasterTelemetryAdmissionClass.Unknown)]
    [InlineData("no-eligible-raster-placement", true, RasterTelemetryAdmissionClass.Health)]
    [InlineData("capability-missing", false, RasterTelemetryAdmissionClass.Capability)]
    [InlineData("mutation-decision-mismatch", false, RasterTelemetryAdmissionClass.Semantic)]
    public void PlanningRefusalClassification_DoesNotInventSpecificCauses(
        string reasonCode,
        bool isRetryable,
        RasterTelemetryAdmissionClass expected)
    {
        Assert.Equal(expected, RasterExecutionTelemetry.ClassifyPlanningRefusal(reasonCode, isRetryable));
    }

    [Theory]
    [InlineData("tenant_id")]
    [InlineData("job_id")]
    [InlineData("attempt_id")]
    [InlineData("process_id")]
    [InlineData("reason")]
    [InlineData("semantic_version")]
    [InlineData("implementation_version")]
    [InlineData("backend")]
    [InlineData("object_uri")]
    [InlineData("object_key")]
    [InlineData("source_url")]
    [InlineData("credential")]
    [InlineData("token")]
    [InlineData("connection_string")]
    [InlineData("sql_text")]
    [InlineData("gdal_command")]
    public void MetricDimensionAllowlist_RejectsIdentifiersLocatorsAndSecrets(string dimension)
    {
        Assert.False(RasterExecutionTelemetry.IsAllowedMetricDimension(dimension));
    }

    [Theory]
    [InlineData("engine")]
    [InlineData("placement")]
    [InlineData("admission")]
    [InlineData("outcome")]
    [InlineData("phase")]
    [InlineData("backend_family")]
    [InlineData("io_operation")]
    [InlineData("cache_result")]
    [InlineData("pricing_model")]
    public void MetricDimensionAllowlist_AcceptsOnlyDocumentedBoundedKeys(string dimension)
    {
        Assert.True(RasterExecutionTelemetry.IsAllowedMetricDimension(dimension));
    }

    [Fact]
    public void EveryMetricTagFactory_UsesOnlyTheDimensionAllowlist()
    {
        var tagSets = new[]
        {
            RasterExecutionTelemetry.CreatePlanningMetricTags(
                RasterEngine.Postgis,
                RasterExecutionPlacement.DurablePostgis,
                RasterTelemetryAdmissionClass.Accepted,
                RasterTelemetryOutcome.Selected),
            RasterExecutionTelemetry.CreateLifecycleMetricTags(
                RasterEngine.Postgis,
                RasterExecutionPlacement.DurablePostgis,
                RasterTelemetryPhase.Execute,
                RasterTelemetryOutcome.Succeeded),
            RasterExecutionTelemetry.CreateArtifactIoMetricTags(
                RasterEngine.GdalNative,
                RasterExecutionPlacement.RemoteBackend,
                RasterTelemetryIoOperation.RangeRead,
                RasterTelemetryOutcome.Succeeded),
            RasterExecutionTelemetry.CreateCacheMetricTags(
                RasterEngine.GdalNative,
                RasterExecutionPlacement.RemoteBackend,
                RasterTelemetryCacheResult.Hit,
                RasterTelemetryOutcome.Succeeded),
            RasterExecutionTelemetry.CreateBatchMetricTags(
                RasterEngine.GdalNative,
                RasterExecutionPlacement.RemoteBackend,
                RasterTelemetryBackendFamily.AwsBatch,
                RasterBatchPricingModel.Spot,
                RasterTelemetryOutcome.Succeeded),
        };

        Assert.All(
            tagSets,
            tags => Assert.All(
                tags,
                tag => Assert.True(RasterExecutionTelemetry.IsAllowedMetricDimension(tag.Key), tag.Key)));
    }

    [Fact]
    public void MetricValueFactories_InvalidEnumsFailClosed()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterExecutionTelemetry.EngineValue((RasterEngine)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterExecutionTelemetry.PlacementValue((RasterExecutionPlacement)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterExecutionTelemetry.AdmissionValue((RasterTelemetryAdmissionClass)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterExecutionTelemetry.OutcomeValue((RasterTelemetryOutcome)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterExecutionTelemetry.PhaseValue((RasterTelemetryPhase)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterExecutionTelemetry.BackendFamilyValue((RasterTelemetryBackendFamily)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterExecutionTelemetry.IoOperationValue((RasterTelemetryIoOperation)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterExecutionTelemetry.CacheResultValue((RasterTelemetryCacheResult)999));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            RasterExecutionTelemetry.PricingModelValue((RasterBatchPricingModel)999));
    }

    [Fact]
    public void InstrumentNames_AreUniqueAndStayInRasterNamespace()
    {
        string[] activities =
        [
            RasterExecutionTelemetry.Activities.Plan,
            RasterExecutionTelemetry.Activities.Submit,
            RasterExecutionTelemetry.Activities.Queue,
            RasterExecutionTelemetry.Activities.Provision,
            RasterExecutionTelemetry.Activities.ResolveSource,
            RasterExecutionTelemetry.Activities.Execute,
            RasterExecutionTelemetry.Activities.Publish,
            RasterExecutionTelemetry.Activities.Register,
            RasterExecutionTelemetry.Activities.Cleanup,
            RasterExecutionTelemetry.Activities.Cancel,
            RasterExecutionTelemetry.Activities.ArtifactIo,
        ];
        string[] metrics =
        [
            RasterExecutionTelemetry.Metrics.PlanningDecisions,
            RasterExecutionTelemetry.Metrics.AdmissionRejections,
            RasterExecutionTelemetry.Metrics.SyncToAsyncPromotions,
            RasterExecutionTelemetry.Metrics.PhaseDuration,
            RasterExecutionTelemetry.Metrics.QueueAge,
            RasterExecutionTelemetry.Metrics.EstimatedCells,
            RasterExecutionTelemetry.Metrics.ActualCells,
            RasterExecutionTelemetry.Metrics.EstimatedBytes,
            RasterExecutionTelemetry.Metrics.ActualBytes,
            RasterExecutionTelemetry.Metrics.DatabaseWork,
            RasterExecutionTelemetry.Metrics.PostgisConnectionWait,
            RasterExecutionTelemetry.Metrics.PostgisSqlDuration,
            RasterExecutionTelemetry.Metrics.PostgisTemporaryBytes,
            RasterExecutionTelemetry.Metrics.ArtifactIoBytes,
            RasterExecutionTelemetry.Metrics.ArtifactIoRequests,
            RasterExecutionTelemetry.Metrics.ArtifactIoDuration,
            RasterExecutionTelemetry.Metrics.SourceResolutionDuration,
            RasterExecutionTelemetry.Metrics.CacheOperations,
            RasterExecutionTelemetry.Metrics.WorkerPeakRss,
            RasterExecutionTelemetry.Metrics.WorkerPeakScratch,
            RasterExecutionTelemetry.Metrics.BatchRequestedVcpus,
            RasterExecutionTelemetry.Metrics.BatchRequestedMemory,
            RasterExecutionTelemetry.Metrics.BatchRequestedGpus,
            RasterExecutionTelemetry.Metrics.BatchAttempts,
            RasterExecutionTelemetry.Metrics.BatchEstimatedCost,
        ];

        Assert.Equal(activities.Length, activities.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(metrics.Length, metrics.Distinct(StringComparer.Ordinal).Count());
        Assert.All(activities, name => Assert.StartsWith("raster.", name, StringComparison.Ordinal));
        Assert.All(metrics, name => Assert.StartsWith("raster.", name, StringComparison.Ordinal));
    }

    [Fact]
    public void ExecutionSummary_ContainsNoCorrelationLocatorOrSecretField()
    {
        var types = new[]
        {
            typeof(RasterExecutionTelemetrySummary),
            typeof(RasterExecutionEstimateSummary),
            typeof(RasterExecutionActualSummary),
            typeof(RasterBatchCostMetadata),
        };
        string[] forbiddenSuffixes = ["Id", "Uri", "Url", "Key", "Secret", "Token", "Credential", "ConnectionString"];

        foreach (var property in types.SelectMany(type => type.GetProperties()))
        {
            Assert.DoesNotContain(
                forbiddenSuffixes,
                suffix => property.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void BatchCostMetadata_SeparatesApproximateCostFromResourceEvidence()
    {
        var metadata = new RasterBatchCostMetadata
        {
            RequestedVcpus = 8,
            RequestedMemoryBytes = 32L * 1024 * 1024 * 1024,
            RequestedGpuCount = 1,
            RequestedScratchBytes = 100L * 1024 * 1024 * 1024,
            AttemptCount = 2,
            ObservedRunSeconds = 120,
            EstimatedCost = 1.25m,
            CurrencyCode = "USD",
            PricingModel = RasterBatchPricingModel.Spot,
            PricingVersion = "aws-price-list-2026-08-01",
            PricedAt = DateTimeOffset.Parse("2026-08-03T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        };

        Assert.Equal(8, metadata.RequestedVcpus);
        Assert.Equal(2, metadata.AttemptCount);
        Assert.Equal(1.25m, metadata.EstimatedCost);
        Assert.Equal("spot", RasterExecutionTelemetry.PricingModelValue(metadata.PricingModel));
    }
}
