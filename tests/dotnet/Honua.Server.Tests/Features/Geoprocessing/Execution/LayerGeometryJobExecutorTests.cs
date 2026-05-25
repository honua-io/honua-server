// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.GeoETL.Services.Connectors;
using Honua.Server.Features.Geoprocessing;
using Honua.Server.Features.Geoprocessing.Execution;
using Honua.Server.Features.Infrastructure.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Unit coverage of the layer-scope (feature-collection) geometry executor.
/// Streams an inline GeoJSON FeatureCollection, applies the geometry operation
/// across every feature (carrying attributes through), and publishes an output
/// FeatureCollection artifact. Runs fully in-memory over NetTopologySuite — no
/// database. Closes the catalog-honesty gap for generalization.simplify-layer,
/// conversion.feature-project, geometry.make-valid, and geometry.difference.
/// </summary>
public sealed class LayerGeometryJobExecutorTests
{
    private const string FeatureCollectionUriPrefix = "data:application/geo+json;base64,";

    private const string TwoPointCollection =
        "{\"type\":\"FeatureCollection\",\"features\":[" +
        "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[0,0]},\"properties\":{\"name\":\"a\"}}," +
        "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Point\",\"coordinates\":[10,10]},\"properties\":{\"name\":\"b\"}}]}";

    [UnitTest]
    public async Task ExecuteAsync_UnsupportedProcessId_FailsWithClassifiedMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");

        var record = CreateJobRecord("geometry.buffer", inputGeoJson: TwoPointCollection);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("layer-scope");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_MakeValid_OverInlineCollection_RepairsEachFeature()
    {
        var collection =
            "{\"type\":\"FeatureCollection\",\"features\":[" +
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":" +
            "[[[0,0],[10,10],[10,0],[0,10],[0,0]]]},\"properties\":{\"id\":1}}]}";

        var (result, published) = await RunAsync("geometry.make-valid", collection);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var doc = ParseCollection(published);
        doc.RootElement.GetProperty("processId").GetString().Should().Be("geometry.make-valid");
        doc.RootElement.GetProperty("featureCount").GetInt64().Should().Be(1);
        var geometry = ReadFirstGeometry(doc);
        geometry.IsValid.Should().BeTrue();
    }

    [UnitTest]
    public async Task ExecuteAsync_SimplifyLayer_CarriesAttributesAndSimplifies()
    {
        var collection =
            "{\"type\":\"FeatureCollection\",\"features\":[" +
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"LineString\",\"coordinates\":" +
            "[[0,0],[5,0.001],[10,0]]},\"properties\":{\"name\":\"line\"}}]}";

        var record = CreateJobRecord(
            "generalization.simplify-layer",
            inputGeoJson: collection,
            extraInputs: new Dictionary<string, string> { ["tolerance"] = "1.0" });

        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-simplify");
        string? published = null;
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => published = call.ArgAt<string>(0));

        var result = await CreateExecutor().ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var doc = ParseCollection(published);
        doc.RootElement.GetProperty("featureCount").GetInt64().Should().Be(1);
        var feature = doc.RootElement.GetProperty("features")[0];
        feature.GetProperty("properties").GetProperty("name").GetString().Should().Be("line");
        var geometry = new GeoJsonReader().Read<Geometry>(feature.GetProperty("geometry").GetRawText());
        ((LineString)geometry).NumPoints.Should().Be(2);
    }

    [UnitTest]
    public async Task ExecuteAsync_FeatureProject_ReprojectsEveryFeature()
    {
        var record = CreateJobRecord(
            "conversion.feature-project",
            inputGeoJson: TwoPointCollection,
            extraInputs: new Dictionary<string, string> { ["fromSrid"] = "4326", ["targetSrid"] = "3857" });

        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-project");
        string? published = null;
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => published = call.ArgAt<string>(0));

        var result = await CreateExecutor().ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var doc = ParseCollection(published);
        doc.RootElement.GetProperty("featureCount").GetInt64().Should().Be(2);
        // (10,10) in WGS84 projects to ~1.11e6 metres in Web Mercator.
        var second = ReadGeometryAt(doc, 1);
        ((Point)second).X.Should().BeApproximately(1113194.9, 1.0);
    }

    [UnitTest]
    public async Task ExecuteAsync_Difference_SubtractsRegionFromEachFeature()
    {
        var collection =
            "{\"type\":\"FeatureCollection\",\"features\":[" +
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":" +
            "[[[0,0],[10,0],[10,10],[0,10],[0,0]]]},\"properties\":{}}]}";

        var record = CreateJobRecord(
            "geometry.difference",
            inputGeoJson: collection,
            extraInputs: new Dictionary<string, string>
            {
                ["regionWkt"] = "POLYGON((5 0, 15 0, 15 10, 5 10, 5 0))"
            });

        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-diff");
        string? published = null;
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => published = call.ArgAt<string>(0));

        var result = await CreateExecutor().ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var doc = ParseCollection(published);
        ReadFirstGeometry(doc).Area.Should().BeApproximately(50.0, 1e-6);
    }

    [UnitTest]
    public async Task ExecuteAsync_Dissolve_GroupsByFieldAndAggregates()
    {
        var collection =
            "{\"type\":\"FeatureCollection\",\"features\":[" +
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":" +
            "[[[0,0],[1,0],[1,1],[0,1],[0,0]]]},\"properties\":{\"region\":\"A\",\"pop\":10}}," +
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":" +
            "[[[1,0],[2,0],[2,1],[1,1],[1,0]]]},\"properties\":{\"region\":\"A\",\"pop\":30}}," +
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":" +
            "[[[10,10],[11,10],[11,11],[10,11],[10,10]]]},\"properties\":{\"region\":\"B\",\"pop\":5}}]}";

        var record = CreateJobRecord(
            "generalization.dissolve",
            inputGeoJson: collection,
            extraInputs: new Dictionary<string, string>
            {
                ["groupByFields"] = "region",
                ["statistics"] = ":count;pop:sum"
            });

        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-dissolve");
        string? published = null;
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => published = call.ArgAt<string>(0));

        var result = await CreateExecutor().ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var doc = ParseCollection(published);
        doc.RootElement.GetProperty("featureCount").GetInt64().Should().Be(2);

        var features = doc.RootElement.GetProperty("features");
        JsonElement? groupA = null;
        foreach (var f in features.EnumerateArray())
        {
            if (f.GetProperty("properties").GetProperty("region").GetString() == "A")
            {
                groupA = f;
            }
        }

        groupA.Should().NotBeNull();
        groupA!.Value.GetProperty("properties").GetProperty("COUNT").GetInt64().Should().Be(2);
        groupA.Value.GetProperty("properties").GetProperty("SUM_pop").GetDouble().Should().Be(40.0);
        var geometry = new GeoJsonReader().Read<Geometry>(groupA.Value.GetProperty("geometry").GetRawText());
        geometry.Area.Should().BeApproximately(2.0, 1e-6);
    }

    [UnitTest]
    public async Task ExecuteAsync_DissolveAll_NoGroupField_CollapsesToOneFeature()
    {
        var collection =
            "{\"type\":\"FeatureCollection\",\"features\":[" +
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":" +
            "[[[0,0],[1,0],[1,1],[0,1],[0,0]]]},\"properties\":{}}," +
            "{\"type\":\"Feature\",\"geometry\":{\"type\":\"Polygon\",\"coordinates\":" +
            "[[[5,5],[6,5],[6,6],[5,6],[5,5]]]},\"properties\":{}}]}";

        var (result, published) = await RunAsync("generalization.dissolve", collection);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var doc = ParseCollection(published);
        doc.RootElement.GetProperty("featureCount").GetInt64().Should().Be(1);
        ReadFirstGeometry(doc).Area.Should().BeApproximately(2.0, 1e-6);
    }

    [UnitTest]
    public async Task ExecuteAsync_MissingInputReference_FailsCleanly()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-noinput");

        var record = CreateJobRecord("geometry.make-valid", inputGeoJson: null);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("input layer reference");
        await context.DidNotReceiveWithAnyArgs().PublishArtifactAsync(default!, default);
    }

    [UnitTest]
    public async Task ExecuteAsync_CatalogLayerWithoutProvider_FailsWithSourceMessage()
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-catalog");

        var record = CreateJobRecord(
            "geometry.make-valid",
            inputGeoJson: null,
            extraInputs: new Dictionary<string, string> { ["layerId"] = "100" });

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("CatalogLayer");
    }

    [UnitTest]
    public async Task ExecuteAsync_SimplifyLayer_MissingTolerance_FailsCleanly()
    {
        var record = CreateJobRecord("generalization.simplify-layer", inputGeoJson: TwoPointCollection);

        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-no-tol");

        var result = await CreateExecutor().ExecuteAsync(record, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("tolerance");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static async Task<(JobExecutionResult Result, string? Published)> RunAsync(
        string processId, string inputGeoJson)
    {
        var executor = CreateExecutor();
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-run");
        string? published = null;
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => published = call.ArgAt<string>(0));

        var record = CreateJobRecord(processId, inputGeoJson);
        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result, published);
    }

    private static LayerGeometryJobExecutor CreateExecutor(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return new LayerGeometryJobExecutor(
            [new InlineGeoJsonLayerFeatureSource()],
            monitor,
            NullLogger<LayerGeometryJobExecutor>.Instance);
    }

    private static JsonDocument ParseCollection(string? published)
    {
        published.Should().NotBeNull();
        published!.Should().StartWith(FeatureCollectionUriPrefix);
        var bytes = Convert.FromBase64String(published[FeatureCollectionUriPrefix.Length..]);
        var doc = JsonDocument.Parse(bytes);
        doc.RootElement.GetProperty("type").GetString().Should().Be("FeatureCollection");
        return doc;
    }

    private static Geometry ReadFirstGeometry(JsonDocument doc) => ReadGeometryAt(doc, 0);

    private static Geometry ReadGeometryAt(JsonDocument doc, int index)
    {
        var feature = doc.RootElement.GetProperty("features")[index];
        return new GeoJsonReader().Read<Geometry>(feature.GetProperty("geometry").GetRawText());
    }

    private static ExecutionJobRecord CreateJobRecord(
        string processId,
        string? inputGeoJson,
        IReadOnlyDictionary<string, string>? extraInputs = null)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            ["protocolProcessId"] = processId,
        };

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        if (inputGeoJson != null)
        {
            parameters[prefix + "inputGeoJson"] = inputGeoJson;
        }

        if (extraInputs != null)
        {
            foreach (var (key, value) in extraInputs)
            {
                parameters[prefix + key] = value;
            }
        }

        return new ExecutionJobRecord
        {
            OperationId = "op-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters
            }
        };
    }
}
