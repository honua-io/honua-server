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
/// In-memory unit coverage for the managed (NetTopologySuite) Hot Spot Analysis
/// executor — the job-dispatchable Getis-Ord Gi* tool over an inline
/// FeatureCollection. The Gi* expectations below are hand-derived closed-form
/// reference values, so the assertions exercise the statistic's correctness
/// rather than echoing the implementation.
/// </summary>
public sealed class ManagedHotSpotExecutorTests
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    [UnitTest]
    public async Task GiStar_MatchesHandDerivedReference_ForAOneDimensionalFixture()
    {
        // Fixture: five collinear points at x = 0..4 (y = 0), values [1, 2, 9, 2, 1],
        // fixed distance band = 1.5 so each point neighbours itself and its immediate
        // neighbour(s). Reference derivation:
        //   n = 5, Σx = 15, X̄ = 3, Σx² = 91, S = sqrt(91/5 - 9) = sqrt(9.2).
        //   Centre (x=2, value 9): W = 3, Σ_N x = 2+9+2 = 13,
        //     z = (13 - 3·3) / (sqrt(9.2)·sqrt((5·3 - 9)/4)) = 4 / (sqrt(9.2)·sqrt(1.5))
        //       = 1.0767648...
        //   Mid (x=1, value 2): W = 3, Σ_N x = 1+2+9 = 12,
        //     z = (12 - 3·3) / (sqrt(9.2)·sqrt(1.5)) = 3 / (sqrt(9.2)·sqrt(1.5)) = 0.8075736...
        //   Endpoint (x=0, value 1): W = 2, Σ_N x = 1+2 = 3,
        //     z = (3 - 3·2) / (sqrt(9.2)·sqrt((5·2 - 4)/4)) = -3 / (sqrt(9.2)·sqrt(1.5))
        //       = -0.8075736...
        var executor = new ManagedHotSpotExecutor(Options());

        var input = BuildUri(
            Feature(Point(0, 0), ("val", 1)),
            Feature(Point(1, 0), ("val", 2)),
            Feature(Point(2, 0), ("val", 9)),
            Feature(Point(3, 0), ("val", 2)),
            Feature(Point(4, 0), ("val", 1)));

        var (status, uri) = await RunAsync(
            executor,
            ManagedHotSpotExecutor.HandledProcessId,
            ("input", input),
            ("field", "val"),
            ("distanceBand", "1.5"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(5, "every located feature is preserved one-to-one");

        var byX = features.ToDictionary(
            f => (int)Math.Round(f.Geometry.Coordinate.X),
            f => f);

        ZScore(byX[2]).Should().BeApproximately(1.0767648, 1e-5);
        ZScore(byX[1]).Should().BeApproximately(0.8075736, 1e-5);
        ZScore(byX[3]).Should().BeApproximately(0.8075736, 1e-5);
        ZScore(byX[0]).Should().BeApproximately(-0.8075736, 1e-5);
        ZScore(byX[4]).Should().BeApproximately(-0.8075736, 1e-5);

        // Two-tailed p-value relationship p = erfc(|z|/√2) must hold for every feature.
        foreach (var feature in features)
        {
            var expectedP = Erfc(Math.Abs(ZScore(feature)) / Math.Sqrt(2.0));
            PValue(feature).Should().BeApproximately(expectedP, 1e-6);
        }
    }

    [UnitTest]
    public async Task GiStar_FlagsAStrongHotSpot_WithA99PercentConfidenceBin()
    {
        // Four high-value (100) points clustered within the distance band, plus 16
        // far-flung isolated low-value (1) points. Reference derivation:
        //   n = 20, Σx = 416, X̄ = 20.8, Σx² = 40016, S = sqrt(40016/20 - 20.8²) = 39.6.
        //   Cluster point: W = 4, Σ_N x = 400,
        //     z = (400 - 20.8·4) / (39.6·sqrt(4·(20-4)/(20-1))) = 316.8 / (39.6·sqrt(64/19))
        //       = 4.358806...  → p ≈ 1.3e-5 → Gi_Bin = +3 (99% hot).
        //   Isolated point: W = 1, Σ_N x = 1,
        //     z = (1 - 20.8) / (39.6·sqrt(1·19/19)) = -19.8 / 39.6 = -0.5 → Gi_Bin = 0.
        var executor = new ManagedHotSpotExecutor(Options());

        var features = new List<IFeature>
        {
            Feature(Point(0, 0), ("val", 100), ("kind", "hot")),
            Feature(Point(0, 1), ("val", 100), ("kind", "hot")),
            Feature(Point(1, 0), ("val", 100), ("kind", "hot")),
            Feature(Point(1, 1), ("val", 100), ("kind", "hot")),
        };
        for (var i = 0; i < 16; i++)
        {
            features.Add(Feature(Point(100.0 + (i * 10.0), 0), ("val", 1), ("kind", "cold")));
        }

        var (status, uri) = await RunAsync(
            executor,
            ManagedHotSpotExecutor.HandledProcessId,
            ("input", BuildUri(features.ToArray())),
            ("field", "val"),
            ("distanceBand", "1.5"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var results = ReadFeatures(uri!);
        results.Should().HaveCount(20);

        var hot = results.First(f =>
            string.Equals(Convert.ToString(f.Attributes.GetOptionalValue("kind"), CultureInfo.InvariantCulture), "hot", StringComparison.Ordinal));
        ZScore(hot).Should().BeApproximately(4.358806, 1e-3);
        PValue(hot).Should().BeLessThan(0.01);
        Bin(hot).Should().Be(3, "a 99%-confidence hot spot is binned +3");

        var cold = results.First(f =>
            string.Equals(Convert.ToString(f.Attributes.GetOptionalValue("kind"), CultureInfo.InvariantCulture), "cold", StringComparison.Ordinal));
        ZScore(cold).Should().BeApproximately(-0.5, 1e-9);
        Bin(cold).Should().Be(0, "an isolated below-mean feature is not statistically significant");
    }

    [UnitTest]
    public async Task NonPointGeometry_IsAnalysedOnItsCentroid_AndAttributesArePreserved()
    {
        var executor = new ManagedHotSpotExecutor(Options());

        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        Polygon Square(double cx, double cy) => factory.CreatePolygon(new[]
        {
            new Coordinate(cx - 0.1, cy - 0.1),
            new Coordinate(cx + 0.1, cy - 0.1),
            new Coordinate(cx + 0.1, cy + 0.1),
            new Coordinate(cx - 0.1, cy + 0.1),
            new Coordinate(cx - 0.1, cy - 0.1),
        });

        var input = BuildUri(
            Feature(Square(0, 0), ("val", 5), ("name", "a")),
            Feature(Square(1, 0), ("val", 6), ("name", "b")),
            Feature(Square(2, 0), ("val", 7), ("name", "c")));

        var (status, uri) = await RunAsync(
            executor,
            ManagedHotSpotExecutor.HandledProcessId,
            ("input", input),
            ("field", "val"),
            ("distanceBand", "1.5"));

        status.Should().Be(ExecutionJobStatus.Succeeded);
        var features = ReadFeatures(uri!);
        features.Should().HaveCount(3);
        features.Should().AllSatisfy(f =>
        {
            f.Attributes.GetOptionalValue("name").Should().NotBeNull();
            f.Attributes.Exists(ManagedHotSpotExecutor.ZScoreAttribute).Should().BeTrue();
            f.Attributes.Exists(ManagedHotSpotExecutor.PValueAttribute).Should().BeTrue();
            f.Attributes.Exists(ManagedHotSpotExecutor.BinAttribute).Should().BeTrue();
            f.Geometry.SRID.Should().Be(4326);
        });
    }

    [UnitTest]
    public async Task SinglePoint_FailsCleanly()
    {
        var executor = new ManagedHotSpotExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedHotSpotExecutor.HandledProcessId,
            ("input", BuildUri(Feature(Point(0, 0), ("val", 1)))),
            ("field", "val"),
            ("distanceBand", "1.5"));

        status.Should().Be(ExecutionJobStatus.Failed, "Gi* is undefined for fewer than two features");
    }

    [UnitTest]
    public async Task AllIdenticalValues_FailCleanly()
    {
        var executor = new ManagedHotSpotExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedHotSpotExecutor.HandledProcessId,
            ("input", BuildUri(
                Feature(Point(0, 0), ("val", 7)),
                Feature(Point(1, 0), ("val", 7)),
                Feature(Point(2, 0), ("val", 7)))),
            ("field", "val"),
            ("distanceBand", "1.5"));

        status.Should().Be(ExecutionJobStatus.Failed, "a zero-variance analysis field has no hot/cold structure");
    }

    [UnitTest]
    public async Task MissingDistanceBand_FailsCleanly()
    {
        var executor = new ManagedHotSpotExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedHotSpotExecutor.HandledProcessId,
            ("input", BuildUri(
                Feature(Point(0, 0), ("val", 1)),
                Feature(Point(1, 0), ("val", 2)))),
            ("field", "val"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    [UnitTest]
    public async Task NonNumericAnalysisValue_FailsCleanly()
    {
        var executor = new ManagedHotSpotExecutor(Options());
        var (status, _) = await RunAsync(
            executor,
            ManagedHotSpotExecutor.HandledProcessId,
            ("input", BuildUri(
                Feature(Point(0, 0), ("val", 1)),
                Feature(Point(1, 0), ("val", "n/a")))),
            ("field", "val"),
            ("distanceBand", "1.5"));

        status.Should().Be(ExecutionJobStatus.Failed);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static double ZScore(IFeature feature)
        => Convert.ToDouble(feature.Attributes.GetOptionalValue(ManagedHotSpotExecutor.ZScoreAttribute), CultureInfo.InvariantCulture);

    private static double PValue(IFeature feature)
        => Convert.ToDouble(feature.Attributes.GetOptionalValue(ManagedHotSpotExecutor.PValueAttribute), CultureInfo.InvariantCulture);

    private static int Bin(IFeature feature)
        => Convert.ToInt32(feature.Attributes.GetOptionalValue(ManagedHotSpotExecutor.BinAttribute), CultureInfo.InvariantCulture);

    private static double Erfc(double x)
    {
        var z = Math.Abs(x);
        var t = 1.0 / (1.0 + (0.5 * z));

        var poly = 0.17087277;
        poly = -0.82215223 + (t * poly);
        poly = 1.48851587 + (t * poly);
        poly = -1.13520398 + (t * poly);
        poly = 0.27886807 + (t * poly);
        poly = -0.18628806 + (t * poly);
        poly = 0.09678418 + (t * poly);
        poly = 0.37409196 + (t * poly);
        poly = 1.00002368 + (t * poly);

        var ans = t * Math.Exp((-z * z) - 1.26551223 + (t * poly));
        return x >= 0.0 ? ans : 2.0 - ans;
    }

    private static IOptionsMonitor<GeoprocessingExecutorOptions> Options()
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
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

    private static string BuildUri(params IFeature[] features)
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
        ManagedHotSpotExecutor executor,
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
