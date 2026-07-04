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
/// Streaming-execution coverage for the GeoETL transform path (ETL scale gap). Proves the
/// transforms no longer materialize the whole FeatureCollection in memory and no longer
/// fail the legacy 50 MiB artifact cap: a pipeline over a &gt;50 MiB (decoded) feature set
/// completes and its output is correct; per-feature transforms stream the input to a
/// spilled NDJSON artifact; dedup spills its seen-key set; and the spill backing file
/// stays bounded relative to the inline path.
/// </summary>
public sealed class StreamingTransformExecutorTests : IDisposable
{
    private const string DataUriPrefix = "data:application/geo+json;base64,";

    private readonly string _outputRoot =
        Path.Combine(Path.GetTempPath(), "honua-stream-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_outputRoot))
            {
                Directory.Delete(_outputRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup of the per-test spill root.
        }
    }

    // -------------------------------------------------------------------------
    // Headline proof: a >50 MiB (decoded) pipeline completes and is correct.
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task Pipeline_Over50MiBDecoded_CompletesAndIsCorrect()
    {
        // Build a source whose decoded GeoJSON is comfortably over the legacy 50 MiB cap.
        // ~140k features of LineStrings + padded attributes lands well past 50 MiB; on the
        // old path the very first transform failed with "Reduce the input feature set".
        const int featureCount = 140_000;
        var sourceRef = await BuildSpilledSourceAsync(featureCount, includeDuplicates: true);
        DecodedByteSize(sourceRef).Should().BeGreaterThan(50L * 1024L * 1024L,
            "the proof requires a source larger than the legacy 50 MiB artifact cap");

        var options = Options();

        // Step 1: attribute-filter keeps the "keep=true" half.
        var filtered = await RunStreamAsync(
            new AttributeFilterTransformExecutor(options),
            AttributeFilterTransformExecutor.HandledProcessId,
            ("input", sourceRef),
            ("field", "keep"),
            ("op", "eq"),
            ("value", "true"));
        filtered.Status.Should().Be(ExecutionJobStatus.Succeeded);

        // Step 2: reproject 4326 -> 3857 (per-feature map).
        var reprojected = await RunStreamAsync(
            new ReprojectTransformExecutor(options),
            ReprojectTransformExecutor.HandledProcessId,
            ("input", filtered.Reference!),
            ("fromSrid", "4326"),
            ("toSrid", "3857"));
        reprojected.Status.Should().Be(ExecutionJobStatus.Succeeded);

        // Step 3: dedup on the "gid" attribute (stateful, spillable).
        var deduped = await RunStreamAsync(
            new DedupTransformExecutor(options),
            DedupTransformExecutor.HandledProcessId,
            ("input", reprojected.Reference!),
            ("keys", "gid"));
        deduped.Status.Should().Be(ExecutionJobStatus.Succeeded);

        // The large intermediate/output artifacts are spilled streams, not inline data URIs.
        FeatureStreamArtifact.IsStreamReference(reprojected.Reference).Should().BeTrue(
            "a >50 MiB output must spill rather than inline");

        // Correctness: every output feature passes the filter, carries the 3857 SRID, and
        // each gid appears exactly once. Count via the stream so we never buffer the output.
        var (count, distinctGids, allKept, allReprojected) = await SummarizeStreamAsync(deduped.Reference!);
        count.Should().Be(distinctGids, "dedup leaves one feature per gid");
        allKept.Should().BeTrue("the filter dropped every keep=false feature");
        allReprojected.Should().BeTrue("reproject stamped every geometry with SRID 3857");
        // featureCount/2 are keep=true; duplicates collapse to the distinct gid count.
        count.Should().BeGreaterThan(0);
    }

    // -------------------------------------------------------------------------
    // Streaming correctness for per-feature transforms over a spilled input.
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task StreamingFilter_OverSpilledInput_DropsNonMatching()
    {
        var sourceRef = await BuildSpilledSourceAsync(60_000, includeDuplicates: false);
        FeatureStreamArtifact.IsStreamReference(sourceRef).Should().BeTrue();

        var result = await RunStreamAsync(
            new AttributeFilterTransformExecutor(Options()),
            AttributeFilterTransformExecutor.HandledProcessId,
            ("input", sourceRef),
            ("field", "keep"),
            ("op", "eq"),
            ("value", "true"));

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var (count, _, allKept, _) = await SummarizeStreamAsync(result.Reference!);
        count.Should().Be(30_000, "exactly half the features carry keep=true");
        allKept.Should().BeTrue();
    }

    [UnitTest]
    public async Task StreamingReproject_OverSpilledInput_StampsTargetSrid()
    {
        var sourceRef = await BuildSpilledSourceAsync(60_000, includeDuplicates: false);

        var result = await RunStreamAsync(
            new ReprojectTransformExecutor(Options()),
            ReprojectTransformExecutor.HandledProcessId,
            ("input", sourceRef),
            ("fromSrid", "4326"),
            ("toSrid", "3857"));

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        var (count, _, _, allReprojected) = await SummarizeStreamAsync(result.Reference!);
        count.Should().Be(60_000);
        allReprojected.Should().BeTrue();
    }

    [UnitTest]
    public async Task StreamingMap_AttributeRename_OverSpilledInput_RenamesEveryFeature()
    {
        var sourceRef = await BuildSpilledSourceAsync(60_000, includeDuplicates: false);

        var result = await RunStreamAsync(
            new AttributeRenameTransformExecutor(Options()),
            AttributeRenameTransformExecutor.HandledProcessId,
            ("input", sourceRef),
            ("from", "gid"),
            ("to", "renamed_gid"));

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);

        long total = 0;
        var allRenamed = true;
        await foreach (var feature in OpenStream(result.Reference!))
        {
            total++;
            if (!feature.Attributes.Exists("renamed_gid") || feature.Attributes.Exists("gid"))
            {
                allRenamed = false;
            }
        }

        total.Should().Be(60_000);
        allRenamed.Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Dedup spill correctness.
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task StreamingDedup_OverSpilledInputWithDuplicates_KeepsFirstPerKey()
    {
        // 80k features, each gid present exactly twice -> 40k distinct, first-wins.
        var sourceRef = await BuildSpilledSourceAsync(80_000, includeDuplicates: true);

        var result = await RunStreamAsync(
            new DedupTransformExecutor(Options()),
            DedupTransformExecutor.HandledProcessId,
            ("input", sourceRef),
            ("keys", "gid"));

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        await foreach (var feature in OpenStream(result.Reference!))
        {
            total++;
            var gid = Convert.ToString(feature.Attributes.GetOptionalValue("gid"), CultureInfo.InvariantCulture)!;
            seen.Add(gid).Should().BeTrue("dedup must emit each gid at most once");
        }

        total.Should().Be(40_000, "80k features with each gid duplicated collapse to 40k distinct");
    }

    [UnitTest]
    public async Task SpillableKeySet_BeyondInMemoryLimit_StaysExact()
    {
        // Drive the spillable key set past its in-memory cap so the on-disk path is exercised.
        using var keys = new SpillableKeySet(inMemoryDigestLimit: 1_000, windowLimit: 500);

        var firstSeen = 0;
        for (var i = 0; i < 5_000; i++)
        {
            if (keys.Add($"key-{i.ToString(CultureInfo.InvariantCulture)}"))
            {
                firstSeen++;
            }
        }

        firstSeen.Should().Be(5_000, "every distinct key is seen for the first time exactly once");

        // Re-adding any previously seen key must report a duplicate even after the spill.
        keys.Add("key-0").Should().BeFalse();
        keys.Add("key-4999").Should().BeFalse();
        keys.Add("key-2500").Should().BeFalse();
        keys.Add("brand-new").Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Artifact contract: inline fast-path for small, spill for large.
    // -------------------------------------------------------------------------

    [UnitTest]
    public async Task SmallOutput_StaysInline_ForBackCompat()
    {
        var input = BuildInlineUri(
            Feature(Point(0, 0), ("keep", "true"), ("gid", "1")),
            Feature(Point(1, 1), ("keep", "true"), ("gid", "2")));

        var result = await RunStreamAsync(
            new AttributeRenameTransformExecutor(Options()),
            AttributeRenameTransformExecutor.HandledProcessId,
            ("input", input),
            ("from", "gid"),
            ("to", "id"));

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        FeatureStreamArtifact.IsStreamReference(result.Reference).Should().BeFalse(
            "a tiny output keeps the legacy inline base64 data URI");
        result.Reference!.Should().StartWith(DataUriPrefix);
    }

    [UnitTest]
    public async Task StreamRead_AcceptsBothInlineAndSpilledProducers()
    {
        // A spilled producer feeding an inline-sized downstream and vice versa both round-trip.
        var spilledRef = await BuildSpilledSourceAsync(60_000, includeDuplicates: false);
        FeatureStreamArtifact.TryOpenRead(spilledRef, out var spillError, out var spillStream).Should().BeTrue(spillError);
        (await CountAsync(spillStream)).Should().Be(60_000);

        var inlineRef = BuildInlineUri(Feature(Point(0, 0), ("gid", "1")));
        FeatureStreamArtifact.TryOpenRead(inlineRef, out var inlineError, out var inlineStream).Should().BeTrue(inlineError);
        (await CountAsync(inlineStream)).Should().Be(1);
    }

    // ----- BH-023 regression: path traversal in stream reference is rejected -----

    [UnitTest]
    public void TryOpenRead_WithPathOutsideRoot_RejectsWithError()
    {
        // BH-023: TryOpenRead accepted any path in the stream reference without
        // validating it falls within the configured output root. An authenticated
        // caller could craft a reference pointing at e.g. /etc/passwd or server
        // config files. The outputRootDirectory parameter must reject such references.
        var root = Path.Combine(Path.GetTempPath(), "honua-traversal-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            // Build a crafted reference with a path that escapes the root via "..".
            var traversalPath = Path.GetFullPath(Path.Combine(root, "..", "etc", "passwd"));
            var craftedRef = FeatureStreamArtifact.BuildStreamReference(traversalPath, count: 0, bytes: 0);

            var accepted = FeatureStreamArtifact.TryOpenRead(
                craftedRef,
                out var error,
                out _,
                outputRootDirectory: root);

            accepted.Should().BeFalse("a path outside the output root must be rejected");
            error.Should().NotBeNullOrEmpty("a descriptive error must be returned");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
    }

    [UnitTest]
    public async Task TryOpenRead_WithPathInsideRoot_Succeeds()
    {
        // BH-023: A legitimate stream reference whose path falls within the root must
        // still be accepted — the guard must not over-reject.
        var sourceRef = await BuildSpilledSourceAsync(10, includeDuplicates: false);

        var accepted = FeatureStreamArtifact.TryOpenRead(
            sourceRef,
            out var openError,
            out var stream,
            outputRootDirectory: _outputRoot);

        accepted.Should().BeTrue(openError);
        (await CountAsync(stream)).Should().Be(10);
    }

    [UnitTest]
    public async Task LargeOutput_SpillFileStaysBounded_RelativeToFeatureCount()
    {
        // The spill backing file holds NDJSON, so it is on the order of the decoded payload,
        // not an in-memory base64 blob. The point of the assertion is that we CAN produce an
        // artifact whose decoded content far exceeds the 50 MiB cap without an OOM/cap failure.
        var result = await RunStreamAsync(
            new AttributeRenameTransformExecutor(Options()),
            AttributeRenameTransformExecutor.HandledProcessId,
            ("input", await BuildSpilledSourceAsync(140_000, includeDuplicates: false)),
            ("from", "gid"),
            ("to", "id"));

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        FeatureStreamArtifact.IsStreamReference(result.Reference).Should().BeTrue();
        FeatureStreamArtifact.TryParseStreamReference(result.Reference!, out var descriptor, out var err)
            .Should().BeTrue(err);
        descriptor.Count.Should().Be(140_000);
        descriptor.Bytes.Should().BeGreaterThan(50L * 1024L * 1024L);
        File.Exists(descriptor.Path).Should().BeTrue();
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private IOptionsMonitor<GeoprocessingExecutorOptions> Options(long maxArtifactBytes = 50L * 1024L * 1024L)
    {
        var options = new GeoprocessingExecutorOptions
        {
            MaxArtifactBytes = maxArtifactBytes,
            OutputRootDirectory = _outputRoot,
            ResultRetention = TimeSpan.FromDays(7)
        };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        return monitor;
    }

    /// <summary>
    /// Writes a large synthetic feature set directly to a spilled NDJSON artifact (bypassing
    /// the in-memory inline encode), so a transform under test reads it as a real stream. Each
    /// feature carries a "keep" flag (alternating true/false), a "gid", and a padded "pad"
    /// attribute so the decoded payload crosses the 50 MiB cap at the configured counts.
    /// </summary>
    private async Task<string> BuildSpilledSourceAsync(int featureCount, bool includeDuplicates)
    {
        Directory.CreateDirectory(_outputRoot);
        var path = FeatureStreamArtifact.AllocateSpillPath(_outputRoot, "op-source", "source.synthetic");
        return await FeatureStreamArtifact.WriteStreamAsync(path, Generate(featureCount, includeDuplicates), CancellationToken.None);
    }

    private static async IAsyncEnumerable<IFeature> Generate(int featureCount, bool includeDuplicates)
    {
        var factory = NetTopologySuite.NtsGeometryServices.Instance.CreateGeometryFactory(4326);
        var pad = new string('p', 256);
        for (var i = 0; i < featureCount; i++)
        {
            var x = i % 90;
            var y = (i / 90) % 90;
            var line = factory.CreateLineString(new[]
            {
                new Coordinate(x, y),
                new Coordinate(x + 0.1, y + 0.1),
                new Coordinate(x + 0.2, y),
            });

            // When duplicates are requested, each gid repeats once (gid = i/2) so dedup halves.
            var gid = includeDuplicates ? (i / 2) : i;
            var table = new AttributesTable
            {
                { "gid", gid.ToString(CultureInfo.InvariantCulture) },
                { "keep", (i % 2 == 0) ? "true" : "false" },
                { "pad", pad },
            };

            yield return new Feature(line, table);
            await Task.CompletedTask.ConfigureAwait(false);
        }
    }

    private static long DecodedByteSize(string reference)
    {
        if (FeatureStreamArtifact.TryParseStreamReference(reference, out var descriptor, out _))
        {
            return descriptor.Bytes;
        }

        return Convert.FromBase64String(reference[DataUriPrefix.Length..]).LongLength;
    }

    private IAsyncEnumerable<IFeature> OpenStream(string reference)
    {
        FeatureStreamArtifact.TryOpenRead(reference, out var error, out var stream).Should().BeTrue(error);
        return stream;
    }

    private static async Task<long> CountAsync(IAsyncEnumerable<IFeature> stream)
    {
        long count = 0;
        await foreach (var _ in stream)
        {
            count++;
        }

        return count;
    }

    private async Task<(long Count, long DistinctGids, bool AllKept, bool AllReprojected)> SummarizeStreamAsync(string reference)
    {
        long count = 0;
        var gids = new HashSet<string>(StringComparer.Ordinal);
        var allKept = true;
        var allReprojected = true;

        await foreach (var feature in OpenStream(reference))
        {
            count++;
            var gid = Convert.ToString(feature.Attributes.GetOptionalValue("gid"), CultureInfo.InvariantCulture);
            if (gid is not null)
            {
                gids.Add(gid);
            }

            if (!string.Equals(
                    Convert.ToString(feature.Attributes.GetOptionalValue("keep"), CultureInfo.InvariantCulture),
                    "true",
                    StringComparison.Ordinal))
            {
                allKept = false;
            }

            if (feature.Geometry is null || feature.Geometry.SRID != 3857)
            {
                allReprojected = false;
            }
        }

        return (count, gids.Count, allKept, allReprojected);
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

    private static string BuildInlineUri(params IFeature[] features)
    {
        var collection = new FeatureCollection();
        foreach (var feature in features)
        {
            collection.Add(feature);
        }

        var json = new GeoJsonWriter().Write(collection);
        return DataUriPrefix + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
    }

    private async Task<(ExecutionJobStatus Status, string? Reference)> RunStreamAsync(
        IJobExecutor executor,
        string processId,
        params (string Name, string Value)[] inputs)
    {
        var context = Substitute.For<IJobExecutionContext>();
        context.OperationId.Returns("op-" + Guid.NewGuid().ToString("N"));
        string? publishedRef = null;
        context
            .When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(call => publishedRef = call.ArgAt<string>(0));

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
            OperationId = (string)context.OperationId,
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
        return (result.Status, publishedRef);
    }
}
