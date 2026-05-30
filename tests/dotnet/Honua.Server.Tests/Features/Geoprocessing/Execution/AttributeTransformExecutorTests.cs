// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.Server.Features.ControlPlane;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NetTopologySuite.Features;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

/// <summary>
/// In-memory unit coverage for the reconciled GeoETL attribute transform
/// executors (transform.attribute-rename, transform.attribute-cast,
/// transform.computed-field, transform.attribute-filter). Each builds an input
/// FeatureCollection, runs the executor, and asserts the published
/// FeatureCollection data URI carries the expected attributes through. Mirrors
/// the slice-5 geometry.dissolve executor test shape.
/// </summary>
public sealed class AttributeTransformExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task AttributeRename_RenamesKey_PreservingValueAndGeometry()
    {
        var executor = new AttributeRenameTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(1, 2), ("old", "v1")),
            Feature(Point(3, 4), ("untouched", 9)));

        var (status, uri) = await RunAsync(
            executor,
            AttributeRenameTransformExecutor.HandledProcessId,
            ("input", input),
            ("from", "old"),
            ("to", "renamed"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);
        features[0].Attributes.Exists("renamed").Should().BeTrue();
        features[0].Attributes.Exists("old").Should().BeFalse();
        features[0].Attributes.GetOptionalValue("renamed").Should().Be("v1");
        features[1].Attributes.Exists("untouched").Should().BeTrue();
    }

    [UnitTest]
    public async Task AttributeCast_CoercesToInt_DropsUncoercibleByDefault()
    {
        var executor = new AttributeCastTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("n", "42")),
            Feature(Point(1, 1), ("n", "not-a-number")));

        var (status, uri) = await RunAsync(
            executor,
            AttributeCastTransformExecutor.HandledProcessId,
            ("input", input),
            ("field", "n"),
            ("to", "int"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1, "the uncoercible row is dropped under the default drop policy");
        Convert.ToInt64(features[0].Attributes.GetOptionalValue("n"), CultureInfo.InvariantCulture).Should().Be(42);
    }

    [UnitTest]
    public async Task AttributeCast_OnErrorKeep_RetainsUncoercibleRow()
    {
        var executor = new AttributeCastTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("n", "7")),
            Feature(Point(1, 1), ("n", "bad")));

        var (status, uri) = await RunAsync(
            executor,
            AttributeCastTransformExecutor.HandledProcessId,
            ("input", input),
            ("field", "n"),
            ("to", "int"),
            ("onError", "keep"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().HaveCount(2);
    }

    [UnitTest]
    public async Task ComputedField_Add_ComputesNumericTarget()
    {
        var executor = new ComputedFieldTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(0, 0), ("a", 10), ("b", 5)));

        var (status, uri) = await RunAsync(
            executor,
            ComputedFieldTransformExecutor.HandledProcessId,
            ("input", input),
            ("target", "sum"),
            ("op", "add"),
            ("left", "a"),
            ("right", "b"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(1);
        Convert.ToDouble(features[0].Attributes.GetOptionalValue("sum"), CultureInfo.InvariantCulture).Should().BeApproximately(15.0, 1e-9);
    }

    [UnitTest]
    public async Task ComputedField_Concat_JoinsFieldsWithSeparator()
    {
        var executor = new ComputedFieldTransformExecutor(Options());
        var input = BuildInputUri(Feature(Point(0, 0), ("first", "a"), ("second", "b")));

        var (status, uri) = await RunAsync(
            executor,
            ComputedFieldTransformExecutor.HandledProcessId,
            ("input", input),
            ("target", "joined"),
            ("op", "concat"),
            ("fields", "first,second"),
            ("separator", "-"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features[0].Attributes.GetOptionalValue("joined").Should().Be("a-b");
    }

    [UnitTest]
    public async Task AttributeFilter_GreaterThan_DropsNonMatchingRows()
    {
        var executor = new AttributeFilterTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("score", 10)),
            Feature(Point(1, 1), ("score", 50)),
            Feature(Point(2, 2), ("score", 100)));

        var (status, uri) = await RunAsync(
            executor,
            AttributeFilterTransformExecutor.HandledProcessId,
            ("input", input),
            ("field", "score"),
            ("op", "gt"),
            ("value", "40"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(2);
    }

    [UnitTest]
    public async Task AttributeFilter_Exists_KeepsOnlyFeaturesWithField()
    {
        var executor = new AttributeFilterTransformExecutor(Options());
        var input = BuildInputUri(
            Feature(Point(0, 0), ("tag", "x")),
            Feature(Point(1, 1), ("other", "y")));

        var (status, uri) = await RunAsync(
            executor,
            AttributeFilterTransformExecutor.HandledProcessId,
            ("input", input),
            ("field", "tag"),
            ("op", "exists"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        ReadFeatures(uri!).Should().HaveCount(1);
    }

    [UnitTest]
    public async Task Transform_UnsupportedProcessId_FailsCleanly()
    {
        var executor = new AttributeRenameTransformExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            "geometry.buffer",
            ("input", BuildInputUri(Feature(Point(0, 0)))),
            ("from", "a"),
            ("to", "b"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task Transform_MissingInput_FailsCleanly()
    {
        var executor = new AttributeRenameTransformExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            AttributeRenameTransformExecutor.HandledProcessId,
            ("from", "a"),
            ("to", "b"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task Transform_MalformedInputUri_FailsCleanly()
    {
        var executor = new AttributeRenameTransformExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            AttributeRenameTransformExecutor.HandledProcessId,
            ("input", "not-a-data-uri"),
            ("from", "a"),
            ("to", "b"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            ResultRetention = TimeSpan.FromDays(7)
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
        var payload = FeatureCollectionArtifactBytes(features);
        return DataUriPrefix + Convert.ToBase64String(payload);
    }

    private static byte[] FeatureCollectionArtifactBytes(IFeature[] features)
    {
        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var json = new GeoJsonWriter().Write(collection);
        return Encoding.UTF8.GetBytes(json);
    }

    private static List<IFeature> ReadFeatures(string dataUri)
    {
        var bytes = Convert.FromBase64String(dataUri[DataUriPrefix.Length..]);
        var json = Encoding.UTF8.GetString(bytes);
        var collection = new GeoJsonReader().Read<FeatureCollection>(json);
        return collection.ToList();
    }

    private static async Task<(ExecutionJobStatus Status, string? Uri)> RunAsync(
        IJobExecutor executor,
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
                Parameters = parameters
            }
        };

        var result = await executor.ExecuteAsync(record, context, CancellationToken.None);
        return (result.Status, publishedUri);
    }
}
