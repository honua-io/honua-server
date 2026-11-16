// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.ControlPlane;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;
using Xunit;
using Xunit.Sdk;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

[Trait("Category", "GeometryFormatExecutionProof")]
public sealed class GeometryFormatConversionExecutionProofTests
{
    private const string ScalarDataUriPrefix = "data:application/json;base64,";

    private const string PolygonWithHoleWkbBase64 =
        "AQMAAAACAAAABQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAkQAAAAAAAAAAAAAAAAAAAJEAAAAAAAAAkQAAAAAAAAAAAAAAAAAAAJEAAAAAAAAAAAAAAAAAAAAAABQAAAAAAAAAAAABAAAAAAAAAAEAAAAAAAAAAQAAAAAAAABBAAAAAAAAAEEAAAAAAAAAQQAAAAAAAABBAAAAAAAAAAEAAAAAAAAAAQAAAAAAAAABA";

    private const string PolygonWithHoleEwkbBase64 =
        "AQMAACDmEAAAAgAAAAUAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJEAAAAAAAAAAAAAAAAAAACRAAAAAAAAAJEAAAAAAAAAAAAAAAAAAACRAAAAAAAAAAAAAAAAAAAAAAAUAAAAAAAAAAAAAQAAAAAAAAABAAAAAAAAAAEAAAAAAAAAQQAAAAAAAABBAAAAAAAAAEEAAAAAAAAAQQAAAAAAAAABAAAAAAAAAAEAAAAAAAAAAQA==";

    private const string ClockwisePolygonWkbBase64 =
        "AQMAAAACAAAABQAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAACRAAAAAAAAAJEAAAAAAAAAkQAAAAAAAACRAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABQAAAAAAAAAAAABAAAAAAAAAAEAAAAAAAAAQQAAAAAAAAABAAAAAAAAAEEAAAAAAAAAQQAAAAAAAAABAAAAAAAAAEEAAAAAAAAAAQAAAAAAAAABA";

    private const string WebMercatorPolygonEwkbBase64 =
        "AQMAACARDwAAAgAAAAUAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJEAAAAAAAAAAAAAAAAAAACRAAAAAAAAAJEAAAAAAAAAAAAAAAAAAACRAAAAAAAAAAAAAAAAAAAAAAAUAAAAAAAAAAAAAQAAAAAAAAABAAAAAAAAAAEAAAAAAAAAQQAAAAAAAABBAAAAAAAAAEEAAAAAAAAAQQAAAAAAAAABAAAAAAAAAAEAAAAAAAAAAQA==";

    // Independently derived from the fixture ordinates, not from any output:
    // shoelace area of the 10x10 exterior minus the 2x2 hole.
    private const double ExpectedArea = 96d;

    private static readonly double[][] ExpectedShell =
        [[0, 0], [10, 0], [10, 10], [0, 10], [0, 0]];

    private static readonly double[][] ExpectedHole =
        [[2, 2], [2, 4], [4, 4], [4, 2], [2, 2]];

    [UnitTest]
    public async Task ConvertsPolygonWithHole_ToEveryAdvertisedEncoding_PreservingTopologyRingsAndSrid()
    {
        var advertised = new BuiltInProcessCatalog()
            .GetProcess(GeometryFormatConvertJobExecutor.HandledProcessId)!
            .Parameters.Single(parameter => parameter.Name == "target")
            .AllowedValues!;
        advertised.Should().BeEquivalentTo(["wkt", "geojson", "wkb", "ewkt"]);

        foreach (var target in advertised)
        {
            var result = await ConvertAsync(PolygonWithHoleEwkbBase64, target);

            result.Status.Should().Be(ExecutionJobStatus.Succeeded, $"target '{target}' is advertised");
            var envelope = result.Envelope!.Value;
            envelope.GetProperty("type").GetString().Should().Be("GeometryFormatResult");
            envelope.GetProperty("processId").GetString().Should().Be("conversion.geometry-format");
            envelope.GetProperty("target").GetString().Should().Be(target);
            envelope.GetProperty("srid").GetInt32().Should().Be(4326);
            envelope.GetProperty("geometryType").GetString().Should().Be("Polygon");

            var value = envelope.GetProperty("value").GetString()!;
            envelope.GetProperty("valueEncoding").GetString()
                .Should().Be(target == "wkb" ? "base64" : "text");

            AssertPolygonContent(Decode(value, target), target);
        }
    }

    [UnitTest]
    public async Task EwktCarriesTheSrid_AndWktGeoJsonDoNot()
    {
        var ewkt = (await ConvertAsync(PolygonWithHoleEwkbBase64, "ewkt")).Envelope!.Value
            .GetProperty("value").GetString()!;
        ewkt.Should().StartWith("SRID=4326;", "EWKT is the only text encoding that carries a SRID");

        var wkt = (await ConvertAsync(PolygonWithHoleEwkbBase64, "wkt")).Envelope!.Value
            .GetProperty("value").GetString()!;
        wkt.Should().NotContain("SRID", "ISO WKT has no SRID member");
        wkt.Should().StartWith("POLYGON");

        var geoJson = (await ConvertAsync(PolygonWithHoleEwkbBase64, "geojson")).Envelope!.Value
            .GetProperty("value").GetString()!;
        using var geoJsonDoc = JsonDocument.Parse(geoJson);
        geoJsonDoc.RootElement.TryGetProperty("crs", out _).Should().BeFalse("RFC 7946 has no CRS member");
        geoJsonDoc.RootElement.GetProperty("type").GetString().Should().Be("Polygon");
    }

    [UnitTest]
    public async Task PlainWkbInput_IsNotGivenAnInventedSrid()
    {
        var ewkt = await ConvertAsync(PolygonWithHoleWkbBase64, "ewkt");
        ewkt.Envelope!.Value.GetProperty("srid").GetInt32().Should().Be(0);
        var value = ewkt.Envelope!.Value.GetProperty("value").GetString()!;
        value.Should().NotContain("SRID=", "a SRID-less input must not be labelled with a fabricated SRID");
        AssertPolygonContent(Decode(value, "ewkt"), "ewkt");

        // The WKB round-trip of a SRID-less input stays plain WKB: no EWKB SRID flag.
        var wkb = await ConvertAsync(PolygonWithHoleWkbBase64, "wkb");
        var payload = wkb.Envelope!.Value.GetProperty("value").GetString()!;
        (Convert.FromBase64String(payload)[4] & 0x20).Should().Be(0);
        AssertPolygonContent(Decode(payload, "wkb"), "wkb");
    }

    [UnitTest]
    public async Task WkbTarget_EmitsStandardWkb_AndReportsTheSridOnTheEnvelopeOnly()
    {
        var wkb = await ConvertAsync(PolygonWithHoleEwkbBase64, "wkb");

        // 'ewkb' is not an advertised target, so an EWKB input must come back as
        // STANDARD WKB: a WKB-only consumer rejects PostGIS's SRID flag or misreads the
        // type word it sets. The SRID survives on the envelope, and 'ewkt' remains the
        // encoding that carries it inside the value.
        var envelope = wkb.Envelope!.Value;
        envelope.GetProperty("srid").GetInt32().Should().Be(4326);
        var bytes = Convert.FromBase64String(envelope.GetProperty("value").GetString()!);
        (bytes[4] & 0x20).Should().Be(0, "the EWKB SRID flag must not be set on a 'wkb' output");

        // The decoded SRID is NTS's factory default for a payload that carries none, so
        // the byte-level flag assertion above is what pins the contract.
        AssertPolygonContent(Decode(envelope.GetProperty("value").GetString()!, "wkb"), "wkb");
    }

    [UnitTest]
    public async Task GeoJsonTarget_RejectsAProjectedInputRatherThanMislocatingIt()
    {
        // RFC 7946 has no CRS member and is always WGS 84 lon/lat, so emitting Web
        // Mercator metres under that label would place the geometry wherever a standard
        // consumer reads metres as degrees.
        var result = await ConvertAsync(WebMercatorPolygonEwkbBase64, "geojson");

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("3857").And.Contain("RFC 7946");
        result.Published.Should().BeNull("a rejected conversion must publish nothing");

        // The same projected input is fine for the encodings that can carry its CRS.
        var ewkt = await ConvertAsync(WebMercatorPolygonEwkbBase64, "ewkt");
        ewkt.Status.Should().Be(ExecutionJobStatus.Succeeded);
        ewkt.Envelope!.Value.GetProperty("value").GetString().Should().StartWith("SRID=3857;");
    }

    [UnitTest]
    public async Task GeoJsonTarget_NormalisesRingWindingToTheRightHandRule()
    {
        // Clockwise-exterior polygons are common (Esri applyEdits, shapefile imports).
        // RFC 7946 section 3.1.6 fixes exterior rings counter-clockwise and holes
        // clockwise, and the raw NTS writer preserves whatever the input carried.
        var geoJson = (await ConvertAsync(ClockwisePolygonWkbBase64, "geojson")).Envelope!.Value
            .GetProperty("value").GetString()!;

        var polygon = (Polygon)new GeoJsonReader().Read<Geometry>(geoJson);
        polygon.Shell.IsCCW.Should().BeTrue("RFC 7946 requires a counter-clockwise exterior ring");
        NetTopologySuite.Algorithm.Orientation.IsCCW(polygon.GetInteriorRingN(0).CoordinateSequence)
            .Should().BeFalse("RFC 7946 requires clockwise holes");
        AssertPolygonContent(polygon, "geojson");
    }

    [UnitTest]
    public async Task UnadvertisedTarget_IsRejectedWithoutPublishingAnArtifact()
    {
        var result = await ConvertAsync(PolygonWithHoleEwkbBase64, "shapefile");

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("'target'");
        result.Published.Should().BeNull("a rejected conversion must publish nothing");
    }

    [UnitTest]
    public void SubmitTimeValidation_RejectsAnUnadvertisedTarget()
    {
        var violations = ValidatePlanTarget("shapefile");
        violations.Should().NotBeEmpty("submit-time validation owns the enum rejection");

        ValidatePlanTarget("ewkt").Should().BeEmpty();
    }

    [UnitTest]
    public void CatalogNowClassifiesTheOperationAsAnExecutableSyncJob()
    {
        var process = new BuiltInProcessCatalog()
            .GetProcess(GeometryFormatConvertJobExecutor.HandledProcessId)!;

        process.ExecutionKind.Should().Be(ProcessExecutionKind.Job);
        process.SupportedExecutionModes.Should().HaveFlag(ProcessExecutionModes.Sync);
        process.SupportedExecutionModes.Should().HaveFlag(ProcessExecutionModes.Async);
        RuntimeProfiles.Normalize(process.RuntimeProfile).Should().Be(RuntimeProfiles.Managed);
    }

    private const string FilledPolygonEwkbBase64 =
        "AQMAACDmEAAAAQAAAAUAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAJEAAAAAAAAAAAAAAAAAAACRAAAAAAAAAJEAAAAAAAAAAAAAAAAAAACRAAAAAAAAAAAAAAAAAAAAAAA==";

    private const string ShiftedPolygonEwkbBase64 =
        "AQMAACDmEAAAAgAAAAUAAAAAAAAAAADwPwAAAAAAAAAAAAAAAAAAJkAAAAAAAAAAAAAAAAAAACZAAAAAAAAAJEAAAAAAAADwPwAAAAAAACRAAAAAAAAA8D8AAAAAAAAAAAUAAAAAAAAAAAAIQAAAAAAAAABAAAAAAAAACEAAAAAAAAAQQAAAAAAAABRAAAAAAAAAEEAAAAAAAAAUQAAAAAAAAABAAAAAAAAACEAAAAAAAAAAQA==";

    [UnitTest]
    public async Task Oracle_DroppedHoleOrShiftedOrdinates_AreRejectedEvenThoughTheOutputParses()
    {
        // Both negatives come from real executions, so each output is a valid,
        // parseable value of the requested encoding on the same SRID.
        var filled = await ConvertAsync(FilledPolygonEwkbBase64, "wkt");
        filled.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var filledGeometry = Decode(filled.Envelope!.Value.GetProperty("value").GetString()!, "wkt");
        filledGeometry.Should().BeOfType<Polygon>();
        filledGeometry.IsValid.Should().BeTrue("the substituted output is well formed, not corrupt");

        Action rejectFilled = () => AssertPolygonContent(filledGeometry, "wkt");
        rejectFilled.Should().Throw<XunitException>("the oracle must reject a conversion that dropped the hole")
            .Which.Message.Should().Contain("preserve the hole");

        var shifted = await ConvertAsync(ShiftedPolygonEwkbBase64, "geojson");
        shifted.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var shiftedGeometry = Decode(shifted.Envelope!.Value.GetProperty("value").GetString()!, "geojson");
        // Ring count, winding and area all survive a translation - the shape is identical
        // and only its position moved, which is exactly what a silent reprojection looks
        // like - so only the literal ordinates catch it.
        ((Polygon)shiftedGeometry).NumInteriorRings.Should().Be(1);
        shiftedGeometry.Area.Should().BeApproximately(ExpectedArea, 1e-9);
        ((Polygon)shiftedGeometry).Shell.IsCCW.Should().BeTrue();

        Action rejectShifted = () => AssertPolygonContent(shiftedGeometry, "geojson");
        rejectShifted.Should().Throw<XunitException>("the oracle must reject displaced ordinates")
            .Which.Message.Should().Contain("ring vertex 0");
    }

    // -------------------------------------------------------------------------
    // Oracle
    // -------------------------------------------------------------------------

    private static void AssertPolygonContent(Geometry decoded, string target)
    {
        var polygon = decoded.Should().BeOfType<Polygon>($"target '{target}' must round-trip a polygon").Subject;
        polygon.NumInteriorRings.Should().Be(1, $"target '{target}' must preserve the hole");
        polygon.Area.Should().BeApproximately(ExpectedArea, 1e-9, $"target '{target}' must preserve exterior minus hole");
        polygon.IsValid.Should().BeTrue();

        AssertRing(polygon.ExteriorRing, ExpectedShell, target, "exterior");
        AssertRing(polygon.GetInteriorRingN(0), ExpectedHole, target, "interior");
    }

    private static void AssertRing(LineString ring, double[][] expected, string target, string label)
    {
        ring.NumPoints.Should().Be(expected.Length, $"target '{target}' {label} ring vertex count");
        for (var i = 0; i < expected.Length; i++)
        {
            ring.GetCoordinateN(i).X.Should().Be(expected[i][0], $"target '{target}' {label} ring vertex {i} X");
            ring.GetCoordinateN(i).Y.Should().Be(expected[i][1], $"target '{target}' {label} ring vertex {i} Y");
        }
    }

    private static Geometry Decode(string value, string target) => target switch
    {
        "wkt" => new WKTReader().Read(value),
        "ewkt" => ReadEwkt(value),
        "geojson" => new GeoJsonReader().Read<Geometry>(value),
        "wkb" => new WKBReader { HandleSRID = true }.Read(Convert.FromBase64String(value)),
        _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported target encoding."),
    };

    private static Geometry ReadEwkt(string value)
    {
        if (!value.StartsWith("SRID=", StringComparison.Ordinal))
        {
            return new WKTReader().Read(value);
        }

        var separator = value.IndexOf(';', StringComparison.Ordinal);
        var srid = int.Parse(value[5..separator], System.Globalization.CultureInfo.InvariantCulture);
        var geometry = new WKTReader().Read(value[(separator + 1)..]);
        geometry.SRID = srid;
        return geometry;
    }

    // -------------------------------------------------------------------------
    // Harness
    // -------------------------------------------------------------------------

    private static async Task<ConversionOutcome> ConvertAsync(string geometryBase64, string target)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = 50L * 1024L * 1024L,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        var executor = new GeometryFormatConvertJobExecutor(
            monitor, NullLogger<GeometryFormatConvertJobExecutor>.Instance);

        string? published = null;
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-geometry-format");
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => published = call.ArgAt<string>(0));

        var prefix = $"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.";
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] =
                GeometryFormatConvertJobExecutor.HandledProcessId,
            ["protocolProcessId"] = GeometryFormatConvertJobExecutor.HandledProcessId,
            [prefix + "geometry"] = geometryBase64,
            [prefix + "target"] = target
        };

        var job = new ExecutionJobRecord
        {
            OperationId = "op-geometry-format",
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:conversion.geometry-format",
                Parameters = parameters
            }
        };

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        JsonElement? envelope = null;
        if (published is not null)
        {
            published.Should().StartWith(ScalarDataUriPrefix);
            var bytes = Convert.FromBase64String(published[ScalarDataUriPrefix.Length..]);
            envelope = JsonDocument.Parse(bytes).RootElement.Clone();
        }

        return new ConversionOutcome(result.Status, result.ErrorMessage, published, envelope);
    }

    private static List<GeoprocessingValidationFailure> ValidatePlanTarget(string target)
    {
        var plan = new AnalysisPlan
        {
            PlanId = "plan-geometry-format",
            IntentId = "intent-geometry-format",
            Steps =
            [
                new AnalysisPlanStep
                {
                    StepId = "step0",
                    Kind = AnalysisPlanStepKind.Geoprocess,
                    ProcessId = GeometryFormatConvertJobExecutor.HandledProcessId,
                    Inputs = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["geometry"] = PolygonWithHoleEwkbBase64,
                        ["target"] = target
                    }
                }
            ]
        };

        return ProcessPlanValidator.Validate(plan, new BuiltInProcessCatalog()).Violations;
    }

    private sealed record ConversionOutcome(
        ExecutionJobStatus Status,
        string? ErrorMessage,
        string? Published,
        JsonElement? Envelope);
}
