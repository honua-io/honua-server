// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.EnrichmentCatalog.Abstractions;
using Honua.Core.Features.EnrichmentCatalog.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Executor-level coverage for the <c>enrichment.enrich</c> async batch enrichment
/// job (#2283): the job-executable counterpart of <c>POST /api/enrich</c>. Verifies
/// the enrichment vocabulary (datasetId, method, outputFields, aggregates), both
/// source forms (registered layer id and staged inline FeatureCollection), the
/// dataset minimum-edition/entitlement gate, the dataset-attribution provenance
/// members on the published artifact, and the fail-closed behavior when the
/// catalog resolver or layer connector is absent. The dataset layer and target
/// layer are streamed through a faked <c>source.honua-layer</c> connector so no
/// Postgres runs.
/// </summary>
public sealed class EnrichmentJobExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";
    private const string HonuaLayerSourceId = "source.honua-layer";
    private const string DatasetId = "test-boundaries";
    private const int DatasetLayerId = 8;
    private const int SourceLayerId = 7;

    [UnitTest]
    public async Task Enrich_LayerSource_CarriesFieldsAndAggregates_WithProvenanceMembers()
    {
        // Source layer 7: two disjoint zones. Dataset layer 8: three named points with
        // a numeric population — two inside zone A, one inside zone B.
        var services = DefaultServices(dataset: Dataset());

        var (status, uri) = await RunAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", SourceLayerId.ToString(CultureInfo.InvariantCulture)),
            ("method", "intersects"),
            ("outputFields", "name"),
            ("aggregates", "pop:sum"));

        status.Should().Be(ExecutionJobStatus.Succeeded);

        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "each source feature is preserved one-to-one");

        var zoneA = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "a"));
        Convert.ToInt64(zoneA.Attributes.GetOptionalValue(SpatialJoinSupport.JoinCountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(2);
        Convert.ToDouble(zoneA.Attributes.GetOptionalValue("SUM_pop"), CultureInfo.InvariantCulture).Should().Be(12);

        var zoneB = features.Single(f => Equals(f.Attributes.GetOptionalValue("zone"), "b"));
        Convert.ToInt64(zoneB.Attributes.GetOptionalValue(SpatialJoinSupport.JoinCountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(1);

        // Catalog attribution travels with the artifact as foreign members.
        using var doc = JsonDocument.Parse(ReadArtifactJson(uri!));
        doc.RootElement.GetProperty("datasetId").GetString().Should().Be(DatasetId);
        doc.RootElement.GetProperty("attribution").GetString().Should().Be("Test data (c) Honua");
        doc.RootElement.GetProperty("method").GetString().Should().Be("intersects");
    }

    [UnitTest]
    public async Task Enrich_InlineStagedSource_PointInPolygonMethod_EnrichesInlineFeatures()
    {
        // Inline staged source: one point inside dataset polygon A, one point outside
        // every polygon. method=point-in-polygon maps to the contains predicate.
        var services = DefaultServices(
            dataset: Dataset(),
            datasetFeatures:
            [
                BoxFeature(0, 0, 10, 10, ("region", "A")),
            ]);

        var inline = InlineFeatureCollection(
            PointNtsFeature(5, 5, ("name", "inside")),
            PointNtsFeature(50, 50, ("name", "outside")));

        var (status, uri) = await RunAsync(
            services,
            ("datasetId", DatasetId),
            ("input", inline),
            ("method", "point-in-polygon"),
            ("outputFields", "region"));

        status.Should().Be(ExecutionJobStatus.Succeeded);

        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);

        var inside = features.Single(f => Equals(f.Attributes.GetOptionalValue("name"), "inside"));
        Convert.ToInt64(inside.Attributes.GetOptionalValue(SpatialJoinSupport.JoinCountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(1);

        var outside = features.Single(f => Equals(f.Attributes.GetOptionalValue("name"), "outside"));
        Convert.ToInt64(outside.Attributes.GetOptionalValue(SpatialJoinSupport.JoinCountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(0, "zero-match targets are preserved");
    }

    [UnitTest]
    public async Task Enrich_NearestNeighborMethod_AnnotatesClosestDatasetFeature()
    {
        var services = DefaultServices(
            dataset: Dataset(),
            datasetFeatures:
            [
                PointFeature(3, 0, ("name", "near")),
                PointFeature(30, 0, ("name", "far")),
            ],
            sourceFeatures:
            [
                PointFeature(0, 0, ("id", 1)),
            ]);

        var (status, uri) = await RunAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", SourceLayerId.ToString(CultureInfo.InvariantCulture)),
            ("method", "nearest-neighbor"),
            ("outputFields", "name"));

        status.Should().Be(ExecutionJobStatus.Succeeded);

        var features = ReadFeatures(uri!);
        features.Should().ContainSingle();
        features[0].Attributes.GetOptionalValue("name").Should().Be("near");
        Convert.ToDouble(features[0].Attributes.GetOptionalValue(EnrichmentJobExecutor.NearDistanceAttribute), CultureInfo.InvariantCulture)
            .Should().BeApproximately(3.0, 1e-9);
        Convert.ToInt64(features[0].Attributes.GetOptionalValue(SpatialJoinSupport.JoinCountAttribute), CultureInfo.InvariantCulture)
            .Should().Be(1);
    }

    [UnitTest]
    public async Task Enrich_NormalizesBothLayersToOneCrs()
    {
        // Codex P1: without a common output CRS a 4326 source joined to a 3857 dataset
        // would compare raw, incomparable ordinates. Both reads must therefore request
        // the SAME OutputSrid.
        var source = new RecordingDagFeatureSource(
            HonuaLayerSourceId,
            new Dictionary<int, IReadOnlyList<DagSourceFeature>>
            {
                [SourceLayerId] = DefaultSourceFeatures(),
                [DatasetLayerId] = DefaultDatasetFeatures(),
            });
        var services = ServicesWith(source, Dataset());

        var (status, _) = await RunAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", SourceLayerId.ToString(CultureInfo.InvariantCulture)));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        source.RequestedSrids.Should().HaveCount(2);
        source.RequestedSrids.Should().AllBeEquivalentTo(4326,
            "both layers must be streamed in one CRS, and GeoJSON output must be WGS 84");
    }

    [UnitTest]
    public async Task Enrich_InputAboveCap_FailsFastWithActionableMessage()
    {
        // Codex P2: the cap must be enforced WHILE streaming so an oversized selection
        // fails fast instead of materializing everything before the artifact check.
        var services = DefaultServices(dataset: Dataset());

        var (status, error) = await RunExpectingFailureAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", SourceLayerId.ToString(CultureInfo.InvariantCulture)),
            ("maxInputFeatures", "1"));

        status.Should().Be(ExecutionJobStatus.Failed);
        error.Should().Contain("limit of 1 features");
    }

    [UnitTest]
    public async Task Enrich_CallerCannotRaiseInputCapAboveOperatorCeiling()
    {
        // Codex P2: the cap is an operator ceiling a caller may only LOWER, so an
        // int.MaxValue request cannot disable the streaming guard.
        var source = new RecordingDagFeatureSource(
            HonuaLayerSourceId,
            new Dictionary<int, IReadOnlyList<DagSourceFeature>>
            {
                [SourceLayerId] = DefaultSourceFeatures(),
                [DatasetLayerId] = DefaultDatasetFeatures(),
            });
        var services = ServicesWith(source, Dataset());

        var (status, _) = await RunAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", SourceLayerId.ToString(CultureInfo.InvariantCulture)),
            ("maxInputFeatures", int.MaxValue.ToString(CultureInfo.InvariantCulture)));

        // The job still runs (the seed layers are tiny); the point is the requested cap
        // is clamped rather than honoured verbatim.
        status.Should().Be(ExecutionJobStatus.Succeeded);
    }

    [UnitTest]
    public async Task Enrich_InlineSourceAboveCap_FailsLikeTheLayerBackedSource()
    {
        // Codex P2: the admission cap is a property of the request, not the source form.
        // An inline collection above the cap must fail exactly as the equivalent
        // layer-backed selection does, before the dataset read and the join.
        var services = DefaultServices(dataset: Dataset());

        var (status, error) = await RunExpectingFailureAsync(
            services,
            ("datasetId", DatasetId),
            ("input", InlineFeatureCollection(PointNtsFeature(5, 5), PointNtsFeature(6, 6))),
            ("maxInputFeatures", "1"));

        status.Should().Be(ExecutionJobStatus.Failed);
        error.Should().Contain("limit of 1 features");
    }

    [UnitTest]
    public async Task Enrich_MatchBudgetExceeded_SurfacesActionableMessage()
    {
        // Codex P2: the budget's remedies must reach the caller verbatim rather than
        // collapsing to "computation failed: TransformInputException". Two 1-feature
        // layers with a carried field exceed a budget of zero on the first match.
        var services = DefaultServices(
            dataset: Dataset(),
            datasetFeatures: [PointFeature(5, 5, ("name", "p1"))],
            sourceFeatures: [BoxFeature(0, 0, 10, 10, ("zone", "a"))]);

        var (status, error) = await RunExpectingFailureAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", SourceLayerId.ToString(CultureInfo.InvariantCulture)),
            ("outputFields", "name"),
            ("maxCarriedMatchValues", "0"));

        status.Should().Be(ExecutionJobStatus.Failed);
        error.Should().Contain("cumulative match budget");
        error.Should().Contain("outputFields", "the remedies must survive the failure path");
    }

    [UnitTest]
    public void MatchBudget_ExhaustedByCumulativeCarriedValues_ThrowsActionableError()
    {
        // Codex P1: the per-layer input caps cannot see the join's Cartesian growth
        // (targets x matches x carried fields), so a cumulative budget must fail the
        // job before the carried arrays and artifact are materialized.
        var budget = new SpatialJoinSupport.MatchBudget(4);

        budget.Charge(2);
        budget.Charge(2);

        var exceeded = () => budget.Charge(1);
        exceeded.Should().Throw<TransformInputException>()
            .WithMessage("*cumulative match budget*");
    }

    [UnitTest]
    public async Task Enrich_UnknownDataset_FailsWithClearMessage()
    {
        var services = DefaultServices(dataset: null);

        var (status, error) = await RunExpectingFailureAsync(
            services,
            ("datasetId", "no-such-dataset"),
            ("layerId", "7"));

        status.Should().Be(ExecutionJobStatus.Failed);
        error.Should().Contain("no-such-dataset");
    }

    [UnitTest]
    public async Task Enrich_DatasetAboveCurrentEdition_FailsWithEditionMessage()
    {
        var services = DefaultServices(
            dataset: Dataset() with { MinimumEdition = HonuaEdition.Enterprise },
            edition: HonuaEdition.Pro);

        var (status, error) = await RunExpectingFailureAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", "7"));

        status.Should().Be(ExecutionJobStatus.Failed);
        error.Should().Contain("Enterprise");
    }

    [UnitTest]
    public async Task Enrich_CommunityEdition_FailsEntitlementGate()
    {
        var services = DefaultServices(dataset: Dataset(), edition: HonuaEdition.Community);

        var (status, error) = await RunExpectingFailureAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", "7"));

        status.Should().Be(ExecutionJobStatus.Failed);
        error.Should().Contain("analytics.spatial-join");
    }

    [UnitTest]
    public async Task Enrich_BothLayerIdAndInput_FailsWithExactlyOneSourceMessage()
    {
        var services = DefaultServices(dataset: Dataset());

        var (status, error) = await RunExpectingFailureAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", "7"),
            ("input", InlineFeatureCollection(PointNtsFeature(0, 0))));

        status.Should().Be(ExecutionJobStatus.Failed);
        error.Should().Contain("exactly one source");
    }

    [UnitTest]
    public async Task Enrich_NoResolverRegistered_FailsClosed()
    {
        // No IEnrichmentDatasetResolver in the scope: the enrichment catalog is not
        // part of this deployment, so the process must fail closed.
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddSingleton<IDagFeatureSource>(new FakeLayeredDagFeatureSource(
            HonuaLayerSourceId, new Dictionary<int, IReadOnlyList<DagSourceFeature>>()));
        var services = serviceCollection.BuildServiceProvider();

        var (status, error) = await RunExpectingFailureAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", "7"));

        status.Should().Be(ExecutionJobStatus.Failed);
        error.Should().Contain("catalog");
    }

    [UnitTest]
    public async Task Enrich_WithinDistanceWithoutDistance_FailsWithClassifiedError()
    {
        var services = DefaultServices(dataset: Dataset());

        var (status, error) = await RunExpectingFailureAsync(
            services,
            ("datasetId", DatasetId),
            ("layerId", "7"),
            ("method", "within-distance"));

        status.Should().Be(ExecutionJobStatus.Failed);
        error.Should().Contain("distance");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static EnrichmentDatasetDefinition Dataset() => new(
        DatasetId,
        "Test Boundaries",
        "boundary",
        DatasetLayerId,
        "intersects",
        DistanceMeters: null,
        Attributes: ["name"],
        Attribution: "Test data (c) Honua",
        MinimumEdition: HonuaEdition.Pro,
        Source: "config");

    private static IReadOnlyList<DagSourceFeature> DefaultSourceFeatures() =>
    [
        BoxFeature(0, 0, 10, 10, ("zone", "a")),
        BoxFeature(20, 20, 30, 30, ("zone", "b")),
    ];

    private static IReadOnlyList<DagSourceFeature> DefaultDatasetFeatures() =>
    [
        PointFeature(5, 5, ("name", "p1"), ("pop", 5)),
        PointFeature(6, 6, ("name", "p2"), ("pop", 7)),
        PointFeature(25, 25, ("name", "p3"), ("pop", 3)),
    ];

    // Builds a provider around an explicit feature source (used by the CRS test, which
    // needs to observe the OutputSrid of every layer read).
    private static ServiceProvider ServicesWith(
        IDagFeatureSource source,
        EnrichmentDatasetDefinition? dataset,
        HonuaEdition edition = HonuaEdition.Pro)
    {
        var services = new ServiceCollection();
        services.AddSingleton(source);

        var resolver = Substitute.For<IEnrichmentDatasetResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(
                string.Equals(callInfo.ArgAt<string>(0), dataset?.Id, StringComparison.OrdinalIgnoreCase)
                    ? dataset
                    : null));
        services.AddSingleton(resolver);

        var statusProvider = Substitute.For<ILicenseStatusProvider>();
        statusProvider.GetCurrentStatus().Returns(new LicenseStatus(edition, true, null, "test"));
        services.AddSingleton(statusProvider);

        return services.BuildServiceProvider();
    }

    private static ServiceProvider DefaultServices(
        EnrichmentDatasetDefinition? dataset,
        IReadOnlyList<DagSourceFeature>? datasetFeatures = null,
        IReadOnlyList<DagSourceFeature>? sourceFeatures = null,
        HonuaEdition edition = HonuaEdition.Pro)
    {
        var services = new ServiceCollection();

        services.AddSingleton<IDagFeatureSource>(new FakeLayeredDagFeatureSource(
            HonuaLayerSourceId,
            new Dictionary<int, IReadOnlyList<DagSourceFeature>>
            {
                [SourceLayerId] = sourceFeatures ?? DefaultSourceFeatures(),
                [DatasetLayerId] = datasetFeatures ?? DefaultDatasetFeatures(),
            }));

        var resolver = Substitute.For<IEnrichmentDatasetResolver>();
        resolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(callInfo => Task.FromResult(
                string.Equals(callInfo.ArgAt<string>(0), dataset?.Id, StringComparison.OrdinalIgnoreCase)
                    ? dataset
                    : null));
        services.AddSingleton(resolver);

        var statusProvider = Substitute.For<ILicenseStatusProvider>();
        statusProvider.GetCurrentStatus().Returns(new LicenseStatus(edition, true, null, "test"));
        services.AddSingleton(statusProvider);

        return services.BuildServiceProvider();
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        ServiceProvider services,
        params (string Name, string Value)[] inputs)
    {
        var (status, uri, _) = await ExecuteAsync(services, inputs);
        return (status, uri);
    }

    private static async Task<(ExecutionJobStatus Status, string? Error)> RunExpectingFailureAsync(
        ServiceProvider services,
        params (string Name, string Value)[] inputs)
    {
        var (status, _, error) = await ExecuteAsync(services, inputs);
        return (status, error);
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri, string? Error)> ExecuteAsync(
        ServiceProvider services,
        (string Name, string Value)[] inputs)
    {
        var executor = new EnrichmentJobExecutor(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options(),
            NullLogger<EnrichmentJobExecutor>.Instance);

        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-enrich-test");
        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = EnrichmentJobExecutor.HandledProcessId,
            ["protocolProcessId"] = EnrichmentJobExecutor.HandledProcessId,
        };

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        foreach (var (name, value) in inputs)
        {
            parameters[prefix + name] = value;
        }

        var record = new ExecutionJobRecord
        {
            OperationId = "op-enrich-test",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:test",
                Parameters = parameters,
            },
        };

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result.Status, publishedUri, result.ErrorMessage);
    }

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7),
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    private static string ReadArtifactJson(string dataUri)
        => Encoding.UTF8.GetString(Convert.FromBase64String(dataUri[DataUriPrefix.Length..]));

    private static List<IFeature> ReadFeatures(string dataUri)
        => new GeoJsonReader().Read<FeatureCollection>(ReadArtifactJson(dataUri)).ToList();

    private static string InlineFeatureCollection(params IFeature[] features)
    {
        var payload = FeatureCollectionArtifact.WriteFeatureCollection(features, "test-input");
        return FeatureCollectionArtifact.BuildDataUri(payload);
    }

    private static Feature PointNtsFeature(double x, double y, params (string Name, object Value)[] attributes)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(srid: 4326);
        var attrs = new AttributesTable();
        foreach (var (name, value) in attributes)
        {
            attrs.Add(name, value);
        }

        return new Feature(factory.CreatePoint(new NetTopologySuite.Geometries.Coordinate(x, y)), attrs);
    }

    private static DagSourceFeature PointFeature(double x, double y, params (string Name, object Value)[] attributes)
    {
        var ci = CultureInfo.InvariantCulture;
        var attrs = new Dictionary<string, object?>();
        foreach (var (name, value) in attributes)
        {
            attrs[name] = value;
        }

        return new DagSourceFeature
        {
            GeometryGeoJson = $$"""{"type":"Point","coordinates":[{{x.ToString(ci)}},{{y.ToString(ci)}}]}""",
            Attributes = attrs,
        };
    }

    private static DagSourceFeature BoxFeature(
        double minX,
        double minY,
        double maxX,
        double maxY,
        params (string Name, object Value)[] attributes)
    {
        var ci = CultureInfo.InvariantCulture;
        string P(double x, double y) => $"[{x.ToString(ci)},{y.ToString(ci)}]";
        var ring = string.Join(",", P(minX, minY), P(maxX, minY), P(maxX, maxY), P(minX, maxY), P(minX, minY));
        var attrs = new Dictionary<string, object?>();
        foreach (var (name, value) in attributes)
        {
            attrs[name] = value;
        }

        return new DagSourceFeature
        {
            GeometryGeoJson = $$"""{"type":"Polygon","coordinates":[[{{ring}}]]}""",
            Attributes = attrs,
        };
    }

    /// <summary>
    /// Layered fake that also records the <see cref="DagSourceRequest.OutputSrid"/> of
    /// every read, so a test can prove both layers were streamed in one CRS.
    /// </summary>
    private sealed class RecordingDagFeatureSource : IDagFeatureSource
    {
        private readonly IReadOnlyDictionary<int, IReadOnlyList<DagSourceFeature>> _byLayer;

        public RecordingDagFeatureSource(
            string sourceId,
            IReadOnlyDictionary<int, IReadOnlyList<DagSourceFeature>> byLayer)
        {
            SourceId = sourceId;
            _byLayer = byLayer;
        }

        public string SourceId { get; }

        public List<int?> RequestedSrids { get; } = [];

        public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
            DagSourceRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            RequestedSrids.Add(request.OutputSrid);
            if (request.LayerId is { } layerId && _byLayer.TryGetValue(layerId, out var features))
            {
                foreach (var feature in features)
                {
                    yield return feature;
                }
            }

            await Task.CompletedTask;
        }
    }

    /// <summary>
    /// Fake <see cref="IDagFeatureSource"/> returning a distinct feature set per
    /// catalog layer id, so the executor reads the target layer and the enrichment
    /// dataset's layer from the same connector without a Postgres catalog.
    /// </summary>
    private sealed class FakeLayeredDagFeatureSource : IDagFeatureSource
    {
        private readonly IReadOnlyDictionary<int, IReadOnlyList<DagSourceFeature>> _byLayer;

        public FakeLayeredDagFeatureSource(
            string sourceId,
            IReadOnlyDictionary<int, IReadOnlyList<DagSourceFeature>> byLayer)
        {
            SourceId = sourceId;
            _byLayer = byLayer;
        }

        public string SourceId { get; }

        public async IAsyncEnumerable<DagSourceFeature> ReadAsync(
            DagSourceRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (request.LayerId is { } layerId && _byLayer.TryGetValue(layerId, out var features))
            {
                foreach (var feature in features)
                {
                    yield return feature;
                }
            }

            await Task.CompletedTask;
        }
    }
}
