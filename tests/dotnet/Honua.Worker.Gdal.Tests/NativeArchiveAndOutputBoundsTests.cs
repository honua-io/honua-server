// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.Core.Configuration;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

/// <summary>
/// Native source.ogr archive budgets (#5469) and vector output size admission (#5470).
/// GDAL is not required: a fake command runner stands in for ogr2ogr, and the
/// assertions are on the host-side guard that runs before and after that call.
/// </summary>
public sealed class NativeArchiveAndOutputBoundsTests : IDisposable
{
    private const string GeoJson = """{"type":"FeatureCollection","features":[]}""";

    private readonly string _scratch = Path.Join(Path.GetTempPath(), "honua-gdal-archive-bounds-" + Guid.NewGuid().ToString("N"));

    public NativeArchiveAndOutputBoundsTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_scratch))
            {
                Directory.Delete(_scratch, recursive: true);
            }
        }
        catch (IOException)
        {
            // Best-effort. A failed assertion should still leave the runner able to clean /tmp.
        }
    }

    [UnitTest]
    public void ArchiveBudgets_MatchManagedImportDefaults()
    {
        var options = new GdalWorkerOptions();

        options.MaxArchiveEntryBytes.Should().Be(FileSizeConstants.FiveHundredMB);
        options.MaxArchiveExtractedBytes.Should().Be(FileSizeConstants.OneGB);
        options.MaxArchiveCompressionRatio.Should().Be(200d);
        options.MaxArchiveEntries.Should().Be(100_000);
    }

    [UnitTest]
    public async Task SourceOgr_HighCompressionRatio_IsRejectedBeforeOgrRuns()
    {
        var zeros = new byte[256 * 1024];
        var zip = CreateZip(CompressionLevel.Optimal, ("roads.geojson", zeros));
        var runner = WriteGeoJsonOutput();

        var result = await ExecuteSourceOgrAsync(runner, zip, Options());

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("compression ratio");
        runner.Invocations.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SourceOgr_PathEscapeEntry_IsRejectedBeforeOgrRuns()
    {
        var zip = CreateZip(
            CompressionLevel.NoCompression,
            ("../escaped.geojson", Encoding.UTF8.GetBytes(GeoJson)),
            ("roads.geojson", Encoding.UTF8.GetBytes(GeoJson)));
        var runner = WriteGeoJsonOutput();

        var result = await ExecuteSourceOgrAsync(runner, zip, Options());

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("invalid entry");
        runner.Invocations.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SourceOgr_DeclaredEntryLargerThanDefaultBudget_IsRejectedBeforeOgrRuns()
    {
        // Header claims more than the managed 500 MiB entry budget. The local payload
        // is one byte, so the compressed archive itself is under MaxArtifactBytes.
        var zip = CraftStoredEntry(
            "roads.geojson",
            declaredUncompressed: (uint)(FileSizeConstants.FiveHundredMB + 1),
            declaredCompressed: (uint)((FileSizeConstants.FiveHundredMB + 1) / 100),
            payload: [0x41]);
        var runner = WriteGeoJsonOutput();

        var result = await ExecuteSourceOgrAsync(runner, zip, Options());

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("uncompressed size");
        runner.Invocations.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SourceOgr_EntryCountAboveConfiguredLimit_IsRejectedBeforeOgrRuns()
    {
        var zip = CreateZip(
            CompressionLevel.NoCompression,
            ("a.geojson", Encoding.UTF8.GetBytes(GeoJson)),
            ("b.geojson", Encoding.UTF8.GetBytes(GeoJson)),
            ("c.geojson", Encoding.UTF8.GetBytes(GeoJson)));
        var runner = WriteGeoJsonOutput();
        var options = Options(maxArchiveEntries: 2);

        var result = await ExecuteSourceOgrAsync(runner, zip, options);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("entry count");
        runner.Invocations.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SourceOgr_AggregateExtractedBytesAboveBudget_IsRejectedBeforeOgrRuns()
    {
        var zip = CreateZip(
            CompressionLevel.NoCompression,
            ("a.geojson", new byte[1000]),
            ("b.geojson", new byte[1000]));
        var runner = WriteGeoJsonOutput();
        var options = Options(maxArchiveEntryBytes: 4_000, maxArchiveExtractedBytes: 1_500, maxArchiveCompressionRatio: 1_000);

        var result = await ExecuteSourceOgrAsync(runner, zip, options);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("total uncompressed");
        runner.Invocations.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SourceOgr_CancellationDuringZipExtraction_DoesNotWriteTheWholeEntry()
    {
        var zip = CreateZip(CompressionLevel.NoCompression, ("roads.geojson", new byte[8 * 1024 * 1024]));
        var runner = WriteGeoJsonOutput();
        using var cts = new CancellationTokenSource();
        var job = SourceJob(zip);
        var context = new CancelOnFirstProgressContext(job.OperationId, cts);
        var executor = NewSourceExecutor(runner, Options());
        long maxSeen = 0;
        var stop = 0;
        var poller = Task.Run(() =>
        {
            while (Volatile.Read(ref stop) == 0)
            {
                long total = 0;
                if (Directory.Exists(_scratch))
                {
                    foreach (var file in Directory.EnumerateFiles(_scratch, "*", SearchOption.AllDirectories))
                    {
                        try
                        {
                            total += new FileInfo(file).Length;
                        }
                        catch (IOException)
                        {
                        }
                        catch (UnauthorizedAccessException)
                        {
                        }
                    }
                }

                if (total > maxSeen)
                {
                    maxSeen = total;
                }
            }
        });

        var act = async () => await executor.ExecuteAsync(job, context, cts.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        Volatile.Write(ref stop, 1);
        await poller;

        maxSeen.Should().BeLessThan(1024 * 1024);
        runner.Invocations.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SourceOgr_BoundedZip_StillReachesOgr()
    {
        var zip = CreateZip(CompressionLevel.NoCompression, ("roads.geojson", Encoding.UTF8.GetBytes(GeoJson)));
        var runner = WriteGeoJsonOutput();

        var result = await ExecuteSourceOgrAsync(runner, zip, Options());

        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        runner.Invocations.Should().ContainSingle();
    }

    [UnitTest]
    public async Task SourceOgr_OutputLargerThanMaxArtifactBytes_IsRejectedBeforeTheWholeFileIsRead()
    {
        var runner = SparseOutput(int.MaxValue + 1L);
        var executor = NewSourceExecutor(runner, Options(maxArtifactBytes: 1024));
        var job = GdalJobFactory.Job(
            GdalVectorSourceReadJobExecutor.HandledProcessId,
            ("source", Convert.ToBase64String(Encoding.UTF8.GetBytes(GeoJson))),
            ("sourceFormat", "GeoJSON"));
        var context = new RecordingJobExecutionContext(job.OperationId);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        context.Artifacts.Should().BeEmpty();
        runner.Invocations.Should().ContainSingle();
    }

    [UnitTest]
    public async Task VectorReproject_OutputLargerThanMaxArtifactBytes_IsRejectedBeforeTheWholeFileIsRead()
    {
        var runner = SparseOutput(int.MaxValue + 1L);
        var executor = new GdalVectorReprojectJobExecutor(runner, Options(maxArtifactBytes: 1024), NullLogger<GdalVectorReprojectJobExecutor>.Instance);
        var job = GdalJobFactory.Job(
            GdalVectorReprojectJobExecutor.HandledProcessId,
            ("input", "data:application/geo+json;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(GeoJson))),
            ("fromSrid", "4267"),
            ("toSrid", "4326"));
        var context = new RecordingJobExecutionContext(job.OperationId);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("MaxArtifactBytes");
        context.Artifacts.Should().BeEmpty();
        runner.Invocations.Should().ContainSingle();
    }

    [UnitTest]
    public async Task SourceOgr_ModestOutputAboveCeiling_IsRejectedWithoutPublishing()
    {
        var runner = WriteGeoJsonOutput(new byte[4096]);
        var executor = NewSourceExecutor(runner, Options(maxArtifactBytes: 1024));
        var job = GdalJobFactory.Job(
            GdalVectorSourceReadJobExecutor.HandledProcessId,
            ("source", Convert.ToBase64String(Encoding.UTF8.GetBytes(GeoJson))),
            ("sourceFormat", "GeoJSON"));
        var context = new RecordingJobExecutionContext(job.OperationId);

        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);

        result.Status.Should().Be(ExecutionJobStatus.Failed);
        result.ErrorMessage.Should().Contain("4096").And.Contain("MaxArtifactBytes");
        context.Artifacts.Should().BeEmpty();
    }

    private async Task<JobExecutionResult> ExecuteSourceOgrAsync(
        FakeGdalCommandRunner runner,
        byte[] zip,
        IOptionsMonitor<GdalWorkerOptions> options)
    {
        var executor = NewSourceExecutor(runner, options);
        var job = SourceJob(zip);
        var context = new RecordingJobExecutionContext(job.OperationId);
        return await executor.ExecuteAsync(job, context, CancellationToken.None);
    }

    private GdalVectorSourceReadJobExecutor NewSourceExecutor(
        FakeGdalCommandRunner runner,
        IOptionsMonitor<GdalWorkerOptions> options)
        => new(runner, options, NullLogger<GdalVectorSourceReadJobExecutor>.Instance);

    private ExecutionJobRecord SourceJob(byte[] zip)
        => GdalJobFactory.Job(
            GdalVectorSourceReadJobExecutor.HandledProcessId,
            ("source", Convert.ToBase64String(zip)),
            ("sourceFormat", "GeoJSON"));

    private StaticOptionsMonitor Options(
        long maxArtifactBytes = 50L * 1024L * 1024L,
        long? maxArchiveEntryBytes = null,
        long? maxArchiveExtractedBytes = null,
        double? maxArchiveCompressionRatio = null,
        int? maxArchiveEntries = null)
    {
        var defaults = new GdalWorkerOptions();
        return new StaticOptionsMonitor(new GdalWorkerOptions
        {
            ScratchRoot = _scratch,
            MaxArtifactBytes = maxArtifactBytes,
            ToolTimeout = TimeSpan.FromMinutes(1),
            MaxArchiveEntryBytes = maxArchiveEntryBytes ?? defaults.MaxArchiveEntryBytes,
            MaxArchiveExtractedBytes = maxArchiveExtractedBytes ?? defaults.MaxArchiveExtractedBytes,
            MaxArchiveCompressionRatio = maxArchiveCompressionRatio ?? defaults.MaxArchiveCompressionRatio,
            MaxArchiveEntries = maxArchiveEntries ?? defaults.MaxArchiveEntries,
        });
    }

    private static FakeGdalCommandRunner WriteGeoJsonOutput(byte[]? output = null)
    {
        var payload = output ?? Encoding.UTF8.GetBytes(GeoJson);
        return new FakeGdalCommandRunner((_, _, workingDirectory) =>
        {
            File.WriteAllBytes(Path.Join(workingDirectory, "output.geojson"), payload);
            return new GdalCommandResult { ExitCode = 0 };
        });
    }

    private static FakeGdalCommandRunner SparseOutput(long length)
        => new((_, _, workingDirectory) =>
        {
            var path = Path.Join(workingDirectory, "output.geojson");
            using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            stream.SetLength(length);
            return new GdalCommandResult { ExitCode = 0 };
        });

    private static byte[] CreateZip(CompressionLevel level, params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name, level);
                using var writer = entry.Open();
                writer.Write(bytes);
            }
        }

        return memory.ToArray();
    }

    private static byte[] CraftStoredEntry(string name, uint declaredUncompressed, uint declaredCompressed, byte[] payload)
    {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        using var memory = new MemoryStream();
        using var writer = new BinaryWriter(memory);
        writer.Write(0x04034b50);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(declaredCompressed);
        writer.Write(declaredUncompressed);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);
        writer.Write(nameBytes);
        writer.Write(payload);
        var localLength = (uint)memory.Length;
        writer.Write(0x02014b50);
        writer.Write((ushort)20);
        writer.Write((ushort)20);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(declaredCompressed);
        writer.Write(declaredUncompressed);
        writer.Write((ushort)nameBytes.Length);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(nameBytes);
        var directoryLength = (uint)(memory.Length - localLength);
        writer.Write(0x06054b50);
        writer.Write((ushort)0);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)1);
        writer.Write(directoryLength);
        writer.Write(localLength);
        writer.Write((ushort)0);
        writer.Flush();
        return memory.ToArray();
    }

    private sealed class CancelOnFirstProgressContext(string operationId, CancellationTokenSource cancellation) : IJobExecutionContext
    {
        public string OperationId { get; } = operationId;

        public Task ReportProgressAsync(double? percentComplete, string? phase, CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            return Task.CompletedTask;
        }

        public Task AppendLogAsync(ExecutionLogEntry entry, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task PublishArtifactAsync(string artifactReference, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class StaticOptionsMonitor(GdalWorkerOptions value) : IOptionsMonitor<GdalWorkerOptions>
    {
        public GdalWorkerOptions CurrentValue { get; } = value;

        public GdalWorkerOptions Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<GdalWorkerOptions, string?> listener) => null;
    }
}
