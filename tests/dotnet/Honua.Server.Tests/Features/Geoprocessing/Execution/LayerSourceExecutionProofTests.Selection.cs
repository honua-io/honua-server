// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.ControlPlane;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Npgsql;
using Xunit.Sdk;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// #4624: the advertised selection family (where, objectIds, geometry/geometryType/inSR/spatialRel,
/// time/timeRelation) and GeoServices outStatistics proven through the production layer read —
/// the real PostGIS feature store, the real <c>source.honua-layer</c> connector and the production
/// <see cref="ILayerSelectionFilterTranslator"/> registration — with row-level security and a field
/// mask enforced in the same read.
/// </summary>
public sealed partial class LayerSourceExecutionProofTests
{
    // Each seeded row is excluded by exactly ONE constraint, so dropping any single constraint
    // changes the selected set:
    //   pop 5 and 7 (zone a) and 100 (zone b) satisfy every selector and the row policy — the
    //   only contributing rows; pop 13 lies outside the time window; pop 50 lies outside the
    //   geometry; pop 9 is not among the objectIds; pop 600 fails where 'pop < 500'; pop 11 is
    //   owned by 'other' and the submitter's row policy is owner = 'ops'.
    private const string SelectionPolygon = """{"rings":[[[9,9],[9,13],[13,13],[13,9],[9,9]]]}""";

    // Every seeded row except pop 9, as the layer's own object ids (resolved after publishing).
    private string _selectionObjectIds = string.Empty;

    private (string Name, string Value)[] AllSelectors =>
    [
        ("where", "pop < 500"),
        ("objectIds", _selectionObjectIds),
        ("geometry", SelectionPolygon),
        ("geometryType", "esriGeometryPolygon"),
        ("inSR", "4326"),
        ("spatialRel", "esriSpatialRelIntersects"),
        ("time", "2026-01-01T00:00:00Z,2026-01-31T00:00:00Z"),
        ("timeRelation", "esriTimeRelationIntersects"),
    ];

    // Row-level security and field-mask stand-ins for the selection layer only. The real
    // PostgresFeatureStore consults them exactly where it consults the production sources, so
    // the store's own AND-combination with the caller's selection is what these proofs exercise.
    private readonly IRowLevelSecurityFilterSource _rls = Substitute.For<IRowLevelSecurityFilterSource>();
    private readonly IFieldMaskSource _masks = Substitute.For<IFieldMaskSource>();
    private int? _selectionLayerId;
    private string? _selectionResourceId;
    private SqlFragment? _selectionRowPolicy;
    private bool _enforceSelectionAuthorization = true;

    private void ConfigureSelectionAuthorization(IServiceCollection services)
    {
        _rls.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(IsGuardedSelectionLayer(call.Arg<MetadataV2Resource>()) ? _selectionRowPolicy : null));
        _masks.ResolveAsync(Arg.Any<MetadataV2Resource>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(IsGuardedSelectionLayer(call.Arg<MetadataV2Resource>())
                ? ImmutableArray.Create("secret")
                : ImmutableArray<string>.Empty));
        services.AddSingleton(_rls);
        services.AddSingleton(_masks);
    }

    private bool IsGuardedSelectionLayer(MetadataV2Resource resource)
        => _enforceSelectionAuthorization && resource.Metadata.Id == _selectionResourceId;

    [IntegrationTest]
    public async Task LayerSelection_EverySelectorFamilyWithRowPolicyAndFieldMask_SelectsExactlyTheIntendedRows()
    {
        var layerId = await EnsureSelectionLayerAsync();
        using var provider = SelectionServices();
        var executor = new LayerSimplifyExecutor(provider.GetRequiredService<IServiceScopeFactory>(),
            ExecutorOptions(), NullLogger<LayerSimplifyExecutor>.Instance);
        (string Name, string Value)[] operation = [("layerId", layerId.ToString(CultureInfo.InvariantCulture)), ("tolerance", "0.000001")];

        // Each selector family alone restricts exactly its own rows, and both authorization
        // restrictions stay additive in every read: the row policy (owner = 'ops') still removes
        // pop 11 and the field mask still hides 'secret'.
        foreach (var (family, expected) in new (string Family, int[] Pops)[]
        {
            ("where", new[] { 5, 7, 100, 13, 50, 9 }),
            ("objectIds", new[] { 5, 7, 100, 13, 50, 600 }),
            ("geometry", new[] { 5, 7, 100, 13, 9, 600 }),
            ("time", new[] { 5, 7, 100, 50, 9, 600 }),
        })
        {
            using var alone = await Execute(executor, "generalization.simplify-layer",
                [.. operation, .. AllSelectors.Where(selector => SelectorFamily(selector.Name) == family)]);
            SelectedPops(alone.RootElement).Should().BeEquivalentTo(expected, $"the {family} selector alone selects exactly its rows");
            foreach (var feature in alone.RootElement.GetProperty("features").EnumerateArray())
            {
                feature.GetProperty("properties").TryGetProperty("secret", out _)
                    .Should().BeFalse($"the field mask applies together with the {family} selector");
            }
        }

        // The relationship modifiers are honored, not only their primary selectors: a
        // non-default timeRelation and spatialRel each select a different, exactly known set.
        using (var after = await Execute(executor, "generalization.simplify-layer",
            [.. operation, ("time", "2026-02-01T00:00:00Z"), ("timeRelation", "esriTimeRelationAfter")]))
        {
            SelectedPops(after.RootElement).Should().BeEquivalentTo(new[] { 13 }, "only the February row starts after 2026-02-01");
        }

        using (var disjoint = await Execute(executor, "generalization.simplify-layer",
            [.. operation, ("geometry", SelectionPolygon), ("geometryType", "esriGeometryPolygon"), ("inSR", "4326"),
                ("spatialRel", "esriSpatialRelDisjoint")]))
        {
            SelectedPops(disjoint.RootElement).Should().BeEquivalentTo(new[] { 50 }, "only the (40,40) row is disjoint from the selection square");
        }

        using (var output = await Execute(executor, "generalization.simplify-layer", [.. operation, .. AllSelectors]))
        {
            AssertSelectedRows(output.RootElement);
        }

        // Plausible wrong results against the same oracle: each selector family silently
        // dropped (a broadened selection), and the row policy / field mask left unenforced.
        foreach (var family in new[] { "where", "objectIds", "geometry", "time" })
        {
            using var wrong = await Execute(executor, "generalization.simplify-layer",
                [.. operation, .. AllSelectors.Where(selector => SelectorFamily(selector.Name) != family)]);
            Action assert = () => AssertSelectedRows(wrong.RootElement);
            assert.Should().Throw<XunitException>($"dropping the {family} selector must fail the selection oracle");
        }

        _enforceSelectionAuthorization = false;
        try
        {
            using var unguarded = await Execute(executor, "generalization.simplify-layer", [.. operation, .. AllSelectors]);
            Action assert = () => AssertSelectedRows(unguarded.RootElement);
            assert.Should().Throw<XunitException>("an unenforced row policy or field mask must fail the selection oracle");
        }
        finally
        {
            _enforceSelectionAuthorization = true;
        }
    }

    [IntegrationTheory]
    [InlineData("polygon-4326")]
    [InlineData("envelope-4326")]
    [InlineData("envelope-3857")]
    public async Task LayerSelection_GeometryEncodingsAndInputSr_SelectTheSameRows(string encoding)
    {
        // The same 9..13 degree square expressed three ways; the Web Mercator envelope is
        // computed here with the spherical Mercator formulas, independently of the server.
        const double radius = 6378137;
        static double MercatorX(double lon) => radius * lon * Math.PI / 180;
        static double MercatorY(double lat) => radius * Math.Log(Math.Tan((Math.PI / 4) + (lat * Math.PI / 360)));
        var (geometry, geometryType, inSr) = encoding switch
        {
            "polygon-4326" => (SelectionPolygon, "esriGeometryPolygon", "4326"),
            "envelope-4326" => ("""{"xmin":9,"ymin":9,"xmax":13,"ymax":13}""", "esriGeometryEnvelope", "4326"),
            _ => (FormattableString.Invariant(
                    $$"""{"xmin":{{MercatorX(9)}},"ymin":{{MercatorY(9)}},"xmax":{{MercatorX(13)}},"ymax":{{MercatorY(13)}}}"""),
                "esriGeometryEnvelope", "3857"),
        };
        var layerId = await EnsureSelectionLayerAsync();
        using var provider = SelectionServices();
        var executor = new LayerSimplifyExecutor(provider.GetRequiredService<IServiceScopeFactory>(),
            ExecutorOptions(), NullLogger<LayerSimplifyExecutor>.Instance);

        using var output = await Execute(executor, "generalization.simplify-layer",
        [
            ("layerId", layerId.ToString(CultureInfo.InvariantCulture)),
            ("tolerance", "0.000001"),
            .. AllSelectors.Where(selector => SelectorFamily(selector.Name) != "geometry"),
            ("geometry", geometry),
            ("geometryType", geometryType),
            ("inSR", inSr),
        ]);

        AssertSelectedRows(output.RootElement);
    }

    [IntegrationTest]
    public async Task LayerSelection_GeoServicesOutStatistics_AggregateOnlyTheSelectedRows()
    {
        // Oracle over the selected rows only — zone a {5, 7}: COUNT 2, SUM 12, AVG 6, COUNT(pop) 2,
        // MAX 7; zone b {100}: COUNT 1, SUM 100, AVG 100, COUNT(pop) 1, MAX 100.
        const string statistics = """
            [{"statisticType":"sum","onStatisticField":"pop","outStatisticFieldName":"total_pop"},
             {"statisticType":"avg","onStatisticField":"pop","outStatisticFieldName":"mean_pop"},
             {"statisticType":"count","onStatisticField":"pop","outStatisticFieldName":"n_pop"},
             {"statisticType":"max","onStatisticField":"pop","outStatisticFieldName":"max_pop"}]
            """;
        var layerId = await EnsureSelectionLayerAsync();
        using var provider = SelectionServices();
        var executor = new LayerDissolveExecutor(provider.GetRequiredService<IServiceScopeFactory>(),
            ExecutorOptions(), NullLogger<LayerDissolveExecutor>.Instance);
        (string Name, string Value)[] operation =
            [("layerId", layerId.ToString(CultureInfo.InvariantCulture)), ("groupByFields", "zone")];

        using (var output = await Execute(executor, "generalization.dissolve",
            [.. operation, ("outStatistics", statistics), .. AllSelectors]))
        {
            AssertSelectedStatistics(output.RootElement);
        }

        // An ignored statistic and a broadened selection both fail the same oracle.
        using (var ignored = await Execute(executor, "generalization.dissolve", [.. operation, .. AllSelectors]))
        {
            Action assert = () => AssertSelectedStatistics(ignored.RootElement);
            assert.Should().Throw<XunitException>("an ignored outStatistics request must fail the statistics oracle");
        }

        using (var broadened = await Execute(executor, "generalization.dissolve",
            [.. operation, ("outStatistics", statistics), .. AllSelectors.Where(selector => SelectorFamily(selector.Name) != "time")]))
        {
            Action assert = () => AssertSelectedStatistics(broadened.RootElement);
            assert.Should().Throw<XunitException>("statistics over a broadened selection must fail the statistics oracle");
        }
    }

    [IntegrationTest]
    public async Task LayerSelection_TimeOnNonTemporalLayer_FailsClosedWithTheCanonicalError()
    {
        var layerId = await EnsureSelectionLayerAsync(timeAware: false);
        using var provider = SelectionServices();
        var executor = new LayerSimplifyExecutor(provider.GetRequiredService<IServiceScopeFactory>(),
            ExecutorOptions(), NullLogger<LayerSimplifyExecutor>.Instance);

        // Published without a temporal extension, the layer declares no temporal fields: the
        // canonical translation refuses the time filter (docs/gis/temporal-animation-api.md)
        // instead of reading the whole layer.
        var result = await ExecuteRaw(executor, "generalization.simplify-layer",
            ("layerId", layerId.ToString(CultureInfo.InvariantCulture)), ("tolerance", "0.000001"),
            ("time", "2026-01-01T00:00:00Z,2026-01-31T00:00:00Z"));

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("not time-aware");
    }

    private static string SelectorFamily(string name) => name switch
    {
        "geometry" or "geometryType" or "inSR" or "spatialRel" => "geometry",
        "time" or "timeRelation" => "time",
        _ => name,
    };

    private static List<int> SelectedPops(JsonElement output)
        => output.GetProperty("features").EnumerateArray()
            .Select(f => f.GetProperty("properties").GetProperty("pop").GetInt32())
            .ToList();

    private static void AssertSelectedRows(JsonElement output)
    {
        output.GetProperty("featureCount").GetInt32().Should().Be(3);
        var features = output.GetProperty("features").EnumerateArray().ToList();
        SelectedPops(output).Should().BeEquivalentTo([5, 7, 100]);
        foreach (var feature in features)
        {
            var properties = feature.GetProperty("properties");
            properties.TryGetProperty("secret", out _).Should().BeFalse("the submitter's field mask hides 'secret'");
            var (x, y) = properties.GetProperty("pop").GetInt32() switch
            {
                5 => (10d, 10d),
                7 => (11d, 11d),
                _ => (12d, 12d),
            };
            var coordinates = feature.GetProperty("geometry").GetProperty("coordinates");
            coordinates[0].GetDouble().Should().Be(x);
            coordinates[1].GetDouble().Should().Be(y);
        }
    }

    private static void AssertSelectedStatistics(JsonElement output)
    {
        output.GetProperty("featureCount").GetInt32().Should().Be(2);
        var groups = output.GetProperty("features").EnumerateArray()
            .ToDictionary(f => f.GetProperty("properties").GetProperty("zone").GetString()!);
        groups.Keys.Should().BeEquivalentTo("a", "b");
        AssertGroup(groups["a"], count: 2, total: 12, mean: 6, values: 2, max: 7, [(10d, 10d), (11d, 11d)]);
        AssertGroup(groups["b"], count: 1, total: 100, mean: 100, values: 1, max: 100, [(12d, 12d)]);
    }

    private static void AssertGroup(
        JsonElement feature, long count, double total, double mean, long values, double max, (double X, double Y)[] ordinates)
    {
        var properties = feature.GetProperty("properties");
        Column(properties, "COUNT").Should().Be(count);
        Column(properties, "total_pop").Should().Be(total);
        Column(properties, "mean_pop").Should().Be(mean);
        Column(properties, "n_pop").Should().Be(values);
        Column(properties, "max_pop").Should().Be(max);
        Ordinates(feature).Should().BeEquivalentTo(ordinates);
    }

    private static double Column(JsonElement properties, string name)
    {
        properties.TryGetProperty(name, out var value).Should().BeTrue($"the output must carry the '{name}' column");
        return value.GetDouble();
    }

    private static List<(double X, double Y)> Ordinates(JsonElement feature)
    {
        var geometry = feature.GetProperty("geometry");
        var coordinates = geometry.GetProperty("coordinates");
        return geometry.GetProperty("type").GetString() == "Point"
            ? [(coordinates[0].GetDouble(), coordinates[1].GetDouble())]
            : coordinates.EnumerateArray().Select(c => (c[0].GetDouble(), c[1].GetDouble())).ToList();
    }

    private ServiceProvider SelectionServices()
    {
        var store = _fixture.GetService<IStreamingFeatureStore>();
        store.GetType().Assembly.GetName().Name.Should().Be("Honua.Postgres");
        var translator = _fixture.GetService<ILayerSelectionFilterTranslator>();
        translator.GetType().Name.Should().Be("AnalyticsLayerSelectionFilterTranslator",
            "the proof must exercise the production selection translator registration");
        var metadata = _fixture.GetService<IMetadataV2GraphProvider>();
        return new ServiceCollection()
            .AddSingleton<IDagFeatureSource>(new HonuaLayerDagSource(store, metadata, translator))
            .AddSingleton(metadata)
            .BuildServiceProvider();
    }

    private async Task<int> EnsureSelectionLayerAsync(bool timeAware = true)
    {
        if (_selectionLayerId is { } existing)
        {
            return existing;
        }

        var schema = _fixture.CurrentSchema!;
        await using (var connection = await _fixture.Postgres.DataSource.OpenConnectionAsync())
        await using (var command = new NpgsqlCommand($"""
            CREATE TABLE "{schema}".gp_selection_proof (
                id bigint PRIMARY KEY, zone varchar(8) NOT NULL, pop integer NOT NULL,
                observed timestamptz NOT NULL, owner varchar(16) NOT NULL, secret text NOT NULL,
                geom geometry(Point,4326) NOT NULL);
            INSERT INTO "{schema}".gp_selection_proof VALUES
                (1, 'a',    5, '2026-01-02T00:00:00Z', 'ops',   'k1', ST_GeomFromEWKT('SRID=4326;POINT(10 10)')),
                (2, 'a',    7, '2026-01-03T00:00:00Z', 'ops',   'k2', ST_GeomFromEWKT('SRID=4326;POINT(11 11)')),
                (3, 'b',  100, '2026-01-04T00:00:00Z', 'ops',   'k3', ST_GeomFromEWKT('SRID=4326;POINT(12 12)')),
                (4, 'a',   13, '2026-02-15T00:00:00Z', 'ops',   'k4', ST_GeomFromEWKT('SRID=4326;POINT(10.5 10.5)')),
                (5, 'a',   50, '2026-01-02T00:00:00Z', 'ops',   'k5', ST_GeomFromEWKT('SRID=4326;POINT(40 40)')),
                (6, 'a',    9, '2026-01-02T00:00:00Z', 'ops',   'k6', ST_GeomFromEWKT('SRID=4326;POINT(11.5 10.5)')),
                (7, 'a',  600, '2026-01-02T00:00:00Z', 'ops',   'k7', ST_GeomFromEWKT('SRID=4326;POINT(10.2 11.8)')),
                (8, 'a',   11, '2026-01-02T00:00:00Z', 'other', 'k8', ST_GeomFromEWKT('SRID=4326;POINT(11.8 10.2)'));
            """, connection))
        {
            await command.ExecuteNonQueryAsync();
        }

        var published = await _fixture.GetService<ILayerPublishingService>().PublishLayerAsync(
            new NpgsqlConnectionStringBuilder(_fixture.Postgres.ConnectionString) { SearchPath = schema + ",public" }.ConnectionString,
            new LayerPublishRequest
            {
                Schema = schema,
                Table = "gp_selection_proof",
                LayerName = "GP selection proof",
                GeometryColumn = "geom",
                PrimaryKey = "id",
                Srid = 4326,
                Fields = ["id", "zone", "pop", "observed", "owner", "secret"],
                ServiceName = "gpselection_" + Guid.NewGuid().ToString("N"),
                Enabled = true
            });

        // Published layers assign their own sequence object ids, so resolve them from the store
        // instead of assuming they equal the table's primary key.
        var seeded = await _fixture.GetService<IFeatureReader>().QueryAsync(published.LayerId, new FeatureQuery());
        var objectIdByPop = seeded.Items.ToDictionary(
            feature => Convert.ToInt32(feature.Attributes["pop"], CultureInfo.InvariantCulture),
            feature => feature.Id);
        objectIdByPop.Keys.Should().BeEquivalentTo([5, 7, 100, 13, 50, 9, 600, 11]);
        _selectionObjectIds = string.Join(",", new[] { 5, 7, 100, 13, 50, 600, 11 }
            .Select(pop => objectIdByPop[pop].ToString(CultureInfo.InvariantCulture)));

        // Make the layer time-aware on 'observed' through the canonical temporal extension.
        var graphStore = _fixture.GetService<IMetadataV2GraphStore>();
        var snapshot = await graphStore.GetCurrentAsync();
        var temporal = snapshot.Index.ResourcesByStorageLayerId[published.LayerId];
        if (timeAware)
        {
            var observed = new MetadataV2Field { Name = "observed", Type = MetadataV2FieldType.DateTime, Nullable = false };
            temporal = temporal with
            {
                SchemaFields = [.. temporal.SchemaFields.Where(field => field.Name != "observed"), observed],
                Temporal = new MetadataV2ResourceTemporal { StartTimeField = "observed" }
            };
            await graphStore.SaveAsync(snapshot.Graph with
            {
                Resources = snapshot.Graph.Resources.Select(r => r.Metadata.Id == temporal.Metadata.Id ? temporal : r).ToArray()
            }, snapshot.Etag);
        }
        else
        {
            temporal.Temporal?.StartTimeField.Should().BeNullOrEmpty("the negative proof needs a layer without temporal fields");
        }

        // The row policy in the form the production row policy source emits: an ArcGIS SQL
        // predicate translated through the canonical filter service for this resource.
        var filterService = _fixture.GetService<IFilterExpressionService>();
        var parsed = filterService.Parse(FilterLanguage.ArcGisSql, "owner = 'ops'");
        parsed.IsSuccess.Should().BeTrue(parsed.ErrorMessage);
        var policy = filterService.Translate(parsed.Expression, temporal);
        policy.IsSuccess.Should().BeTrue(policy.ErrorMessage);

        _selectionRowPolicy = policy.SqlFilter;
        _selectionResourceId = temporal.Metadata.Id;
        _selectionLayerId = published.LayerId;
        return published.LayerId;
    }

    private static async Task<JobExecutionResult> ExecuteRaw(LayerSimplifyExecutor executor, string id, params (string Key, string Value)[] inputs)
    {
        var parameters = new Dictionary<string, string>
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = id,
            ["protocolProcessId"] = id
        };
        foreach (var (key, value) in inputs)
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0." + key] = value;
        }

        var job = new ExecutionJobRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:" + id,
                Parameters = parameters
            }
        };
        return await executor.ExecuteAsync(job, Substitute.For<IJobExecutionContext>(), CancellationToken.None);
    }
}
