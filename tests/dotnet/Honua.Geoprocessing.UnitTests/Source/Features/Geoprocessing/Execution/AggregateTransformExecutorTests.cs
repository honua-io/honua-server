// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// Regression coverage for BH2-016: AggregateTransformExecutor.BuildGroupKey must escape
/// U+001F/U+001E within field values so that distinct groups whose string values straddle
/// the separator boundary are not merged into the same aggregate bucket.
/// </summary>
public sealed class AggregateTransformExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    // U+001F is the unit-separator character that BuildGroupKey uses between fields.
    private const char UnitSeparator = '';

    [UnitTest]
    public async Task BuildGroupKey_SeparatorCharInFieldValue_ProducesSeparateGroupsNotMerged()
    {
        // Single-field groupBy where the two values differ but each CONTAINS the U+001F
        // separator. Without escaping, a field value containing U+001F is indistinguishable
        // from a composite key boundary, so distinct values could collapse into one bucket.
        var executor = new AggregateTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("city", $"San{UnitSeparator}Jose")),
            Feature(Point(1, 1), ("city", $"San{UnitSeparator}Mateo")));

        var (status, uri) = await RunAsync(
            executor,
            AggregateTransformExecutor.HandledProcessId,
            ("input", input),
            ("groupBy", "city"),
            ("aggregates", "city:count:CNT"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "distinct city values must produce separate groups even when they contain the U+001F separator character");
        features.Should().AllSatisfy(f =>
            Convert.ToInt64(f.Attributes.GetOptionalValue("CNT"), CultureInfo.InvariantCulture)
                .Should().Be(1, "each group has exactly one member"));
    }

    [UnitTest]
    public async Task BuildGroupKey_MultiFieldGroupBy_AmbiguousValuesStraddle_ProducesSeparateGroups()
    {
        // Two features whose 2-field group-by values "straddle" the field boundary:
        // {"San", "Jose"} vs {"San<US>Jose", ""}. Without escaping, both serialize to the
        // same composite key ("San" + U+001F + "Jose") and are wrongly merged into one bucket.
        var executor = new AggregateTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("first", "San"), ("last", "Jose")),
            Feature(Point(1, 1), ("first", $"San{UnitSeparator}Jose"), ("last", "")));

        var (status, uri) = await RunAsync(
            executor,
            AggregateTransformExecutor.HandledProcessId,
            ("input", input),
            ("groupBy", "first,last"),
            ("aggregates", "*:count:CNT"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2, "the two rows belong to distinct groups and must not be collapsed by a key collision across the field boundary");
        features.Should().AllSatisfy(f =>
            Convert.ToInt64(f.Attributes.GetOptionalValue("CNT"), CultureInfo.InvariantCulture)
                .Should().Be(1));
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

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

    private static Point Point(double x, double y)
        => NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326).CreatePoint(new Coordinate(x, y));

    private static Feature Feature(Geometry geometry, params (string Name, object Value)[] attributes)
    {
        var table = new AttributesTable();
        foreach (var (name, value) in attributes)
        {
            table.Add(name, value);
        }

        return new Feature(geometry, table);
    }

    private static string BuildInputUri(params IFeature[] features)
    {
        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var json = new GeoJsonWriter().Write(collection);
        return DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        return new GeoJsonReader().Read<FeatureCollection>(json).ToList();
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        AggregateTransformExecutor executor,
        string processId,
        params (string Name, string Value)[] inputs)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-test");
        string? publishedUri = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedUri = call.ArgAt<string>(0));

        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = processId,
            ["protocolProcessId"] = processId,
        };

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        foreach (var (name, value) in inputs)
        {
            parameters[prefix + name] = value;
        }

        var record = new ExecutionJobRecord
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
                Parameters = parameters,
            },
        };

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result.Status, publishedUri);
    }
}
