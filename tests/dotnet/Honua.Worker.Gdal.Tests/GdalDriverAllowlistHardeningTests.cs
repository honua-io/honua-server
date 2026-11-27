// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.IO.Compression;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.TestKit.Attributes;
using Honua.Worker.Gdal.Execution;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Honua.Worker.Gdal.Tests;

public sealed class GdalDriverAllowlistHardeningTests : IDisposable
{
    private const string GeoJson = """{"type":"FeatureCollection","features":[]}""";

    private readonly string _scratch = Path.Join(
        Path.GetTempPath(),
        "honua-gdal-allowlist-" + Guid.NewGuid().ToString("N"));

    public GdalDriverAllowlistHardeningTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose() => GdalCli.CleanupScratch(_scratch);

    [UnitTest]
    public void BuildEnvironment_DisablesPamSidecars_ForLocalRemoteAndDocker()
    {
        var localEnv = GdalRuntimeHardening.BuildEnvironment(new GdalHardeningOptions(), inputReferencesRemoteVsi: false);
        var remoteEnv = GdalRuntimeHardening.BuildEnvironment(new GdalHardeningOptions(), inputReferencesRemoteVsi: true);

        localEnv["GDAL_PAM_ENABLED"].Should().Be("NO");
        remoteEnv["GDAL_PAM_ENABLED"].Should().Be("NO");

        var dockerArgs = DockerGdalCommandRunner.BuildDockerRunArguments(
            new GdalContainerExecutionOptions(),
            "gdalinfo",
            ["-json", "/scratch/input.tif"],
            "/scratch",
            localEnv);
        dockerArgs.Should().ContainInOrder("-e", "GDAL_PAM_ENABLED");
    }

    [UnitTest]
    public void VectorDriverAllowlist_ResolvesFormatsAndPrefixesArguments()
    {
        var kml = GdalVectorDriverAllowlist.ResolveFormat("KML");
        kml.Should().NotBeNull();
        kml!.Value.Drivers.Should().Equal("LIBKML", "KML");

        var prefixed = GdalVectorDriverAllowlist.PrefixArguments(kml.Value.Drivers, ["-f", "GeoJSON", "out.geojson", "in.kml"]);
        prefixed.Should().Equal("-if", "LIBKML", "-if", "KML", "-f", "GeoJSON", "out.geojson", "in.kml");

        GdalVectorDriverAllowlist.ResolveFormat("OGR_VRT").Should().BeNull();
        GdalVectorDriverAllowlist.ResolveDriversForPath("/tmp/data.gdb").Should().Equal("OpenFileGDB");

        var empty = () => GdalVectorDriverAllowlist.PrefixArguments([], ["-f", "GeoJSON"]);
        empty.Should().Throw<InvalidOperationException>().WithMessage("*allowlist is empty*");
    }

    [UnitTest]
    public async Task VectorConvert_PassesExplicitIfDriver_AndRejectsUnsupportedSourceFormat()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes("col\n1\n"), outputArgIndexFromEnd: 1);
        var executor = new GdalVectorConvertJobExecutor(
            runner, GdalJobFactory.Options(_scratch), NullLogger<GdalVectorConvertJobExecutor>.Instance);

        var okJob = GdalJobFactory.Job(
            GdalVectorConvertJobExecutor.HandledProcessId,
            ("source", GdalCli.Base64(GeoJson)),
            ("sourceFormat", "GeoJSON"),
            ("targetFormat", "CSV"));
        var okResult = await executor.ExecuteAsync(okJob, new RecordingJobExecutionContext(okJob.OperationId), default);

        okResult.Status.Should().Be(ExecutionJobStatus.Succeeded, okResult.ErrorMessage);
        runner.Invocations.Single().Arguments.Should().ContainInOrder("-if", "GeoJSON", "-f", "CSV");

        var badJob = GdalJobFactory.Job(
            GdalVectorConvertJobExecutor.HandledProcessId,
            ("source", GdalCli.Base64(GeoJson)),
            ("sourceFormat", "VRT"),
            ("targetFormat", "CSV"));
        var badResult = await executor.ExecuteAsync(badJob, new RecordingJobExecutionContext(badJob.OperationId), default);

        badResult.Status.Should().Be(ExecutionJobStatus.Failed);
        badResult.ErrorMessage.Should().Contain("allowlist");
        runner.Invocations.Should().ContainSingle();
    }

    [UnitTest]
    public async Task VectorReproject_PassesExplicitGeoJsonIfDriver()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes(GeoJson), outputArgIndexFromEnd: 1);
        var executor = new GdalVectorReprojectJobExecutor(
            runner, GdalJobFactory.Options(_scratch), NullLogger<GdalVectorReprojectJobExecutor>.Instance);

        var job = GdalJobFactory.Job(
            GdalVectorReprojectJobExecutor.HandledProcessId,
            ("input", "data:application/geo+json;base64," + GdalCli.Base64(GeoJson)),
            ("fromSrid", "4267"),
            ("toSrid", "4326"));
        var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        runner.Invocations.Single().Arguments.Should().ContainInOrder("-if", "GeoJSON", "-f", "GeoJSON");
    }

    [UnitTest]
    public async Task SourceRead_PassesExplicitIfDriver_AndRejectsUnsupportedFormat()
    {
        var runner = FakeGdalCommandRunner.Succeeding(Encoding.UTF8.GetBytes(GeoJson), outputArgIndexFromEnd: 1);
        var executor = new GdalVectorSourceReadJobExecutor(
            runner, GdalJobFactory.Options(_scratch), NullLogger<GdalVectorSourceReadJobExecutor>.Instance);

        var kmlJob = GdalJobFactory.Job(
            GdalVectorSourceReadJobExecutor.HandledProcessId,
            ("source", GdalCli.Base64("<kml/>")),
            ("sourceFormat", "KML"));
        var kmlResult = await executor.ExecuteAsync(kmlJob, new RecordingJobExecutionContext(kmlJob.OperationId), default);

        kmlResult.Status.Should().Be(ExecutionJobStatus.Succeeded, kmlResult.ErrorMessage);
        runner.Invocations.Single().Arguments.Should().ContainInOrder("-if", "LIBKML", "-if", "KML", "-f", "GeoJSON");

        var unsupportedJob = GdalJobFactory.Job(
            GdalVectorSourceReadJobExecutor.HandledProcessId,
            ("source", GdalCli.Base64(GeoJson)),
            ("sourceFormat", "PostgreSQL"));
        var unsupportedResult = await executor.ExecuteAsync(
            unsupportedJob, new RecordingJobExecutionContext(unsupportedJob.OperationId), default);

        unsupportedResult.Status.Should().Be(ExecutionJobStatus.Failed);
        unsupportedResult.ErrorMessage.Should().Contain("allowlist");
        runner.Invocations.Should().ContainSingle();
    }

    [UnitTest]
    public async Task SourceRead_ZipWithHostileSidecarOrFileGdbMember_IsRefusedBeforeOgrRuns()
    {
        var runner = FakeGdalCommandRunner.Failing(1, "ogr2ogr must not run");
        var executor = new GdalVectorSourceReadJobExecutor(
            runner, GdalJobFactory.Options(_scratch), NullLogger<GdalVectorSourceReadJobExecutor>.Instance);

        var hostileSidecarZip = CreateZip(
            ("roads.shp", [0x00, 0x00, 0x27, 0x0A]),
            ("roads.prj", Encoding.UTF8.GetBytes("/vsicurl/https://example.invalid/evil.prj")));
        var sidecarJob = GdalJobFactory.Job(
            GdalVectorSourceReadJobExecutor.HandledProcessId,
            ("source", Convert.ToBase64String(hostileSidecarZip)),
            ("sourceFormat", "ESRI Shapefile"));
        var sidecarResult = await executor.ExecuteAsync(sidecarJob, new RecordingJobExecutionContext(sidecarJob.OperationId), default);

        sidecarResult.Status.Should().Be(ExecutionJobStatus.Failed);
        sidecarResult.ErrorMessage.Should().Contain("roads.prj").And.Contain("/vsicurl");

        var hostileGdbZip = CreateZip(
            ("survey.gdb/a00000001.gdbtable", Encoding.UTF8.GetBytes("<OGRVRTDataSource/>")));
        var gdbJob = GdalJobFactory.Job(
            GdalVectorSourceReadJobExecutor.HandledProcessId,
            ("source", Convert.ToBase64String(hostileGdbZip)),
            ("sourceFormat", "OpenFileGDB"));
        var gdbResult = await executor.ExecuteAsync(gdbJob, new RecordingJobExecutionContext(gdbJob.OperationId), default);

        gdbResult.Status.Should().Be(ExecutionJobStatus.Failed);
        gdbResult.ErrorMessage.Should().Contain("a00000001.gdbtable").And.Contain("<OGRVRTDataSource");
        runner.Invocations.Should().BeEmpty();
    }

    [UnitTest]
    public async Task SourceRead_ZipShapefile_PassesShapefileIfDriver()
    {
        var runner = new FakeGdalCommandRunner((_, _, workDir) =>
        {
            File.WriteAllBytes(Path.Join(workDir, "output.geojson"), Encoding.UTF8.GetBytes(GeoJson));
            return new GdalCommandResult { ExitCode = 0 };
        });
        var executor = new GdalVectorSourceReadJobExecutor(
            runner, GdalJobFactory.Options(_scratch), NullLogger<GdalVectorSourceReadJobExecutor>.Instance);

        var zip = CreateZip(
            ("roads.shp", [0x00, 0x00, 0x27, 0x0A]),
            ("roads.dbf", [0x03, 0x00, 0x00, 0x00]));
        var job = GdalJobFactory.Job(
            GdalVectorSourceReadJobExecutor.HandledProcessId,
            ("source", Convert.ToBase64String(zip)),
            ("sourceFormat", "GeoJSON"));

        var result = await executor.ExecuteAsync(job, new RecordingJobExecutionContext(job.OperationId), default);

        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        runner.Invocations.Single().Arguments.Should().ContainInOrder("-if", "ESRI Shapefile", "-f", "GeoJSON");
    }

    [UnitTest]
    public async Task PythonHelperCallers_PassAllowedDriversToScripts()
    {
        var statsRunner = new FakeGdalCommandRunner((_, _, _) => new GdalCommandResult
        {
            ExitCode = 0,
            StandardOutput = """{"bands":[{"band":1,"type":"Byte","validCount":4,"minimum":0,"maximum":1,"mean":0.5,"stdDev":0.5}]}""",
        });
        var statsExecutor = new GdalRasterStatisticsJobExecutor(
            statsRunner, GdalJobFactory.Options(_scratch), NullLogger<GdalRasterStatisticsJobExecutor>.Instance);
        var statsJob = GdalJobFactory.Job(
            GdalRasterStatisticsJobExecutor.StatisticsProcessId,
            ("source", GdalCli.RasterBase64("stats")));
        var statsResult = await statsExecutor.ExecuteAsync(statsJob, new RecordingJobExecutionContext(statsJob.OperationId), default);

        statsResult.Status.Should().Be(ExecutionJobStatus.Succeeded, statsResult.ErrorMessage);
        statsRunner.Invocations.Single(i => i.Tool == "python3").Arguments
            .Should().Contain("--allowed-drivers=GTiff,PNG,JPEG");

        var allocRunner = new FakeGdalCommandRunner((_, args, _) =>
        {
            File.WriteAllBytes(args.First(a => a.EndsWith("output.tif", StringComparison.Ordinal)), [1, 2, 3]);
            return new GdalCommandResult { ExitCode = 0 };
        });
        var allocExecutor = new GdalProximityJobExecutor(
            allocRunner, GdalJobFactory.Options(_scratch), NullLogger<GdalProximityJobExecutor>.Instance);
        var allocJob = GdalJobFactory.Job(
            GdalProximityJobExecutor.AllocationProcessId,
            ("source", GdalCli.RasterBase64("alloc")));
        var allocResult = await allocExecutor.ExecuteAsync(allocJob, new RecordingJobExecutionContext(allocJob.OperationId), default);

        allocResult.Status.Should().Be(ExecutionJobStatus.Succeeded, allocResult.ErrorMessage);
        allocRunner.Invocations.Single().Arguments.Should().ContainInOrder("--allowed-drivers", "GTiff,PNG,JPEG");
    }

    [UnitTest]
    public void PythonHelperScripts_UseOpenExWithAllowedDrivers_AndNoRawGdalOpen()
    {
        var scriptsDir = Path.Join(AppContext.BaseDirectory, "Scripts");
        var scripts = Directory.GetFiles(scriptsDir, "*.py");
        scripts.Should().NotBeEmpty();

        foreach (var script in scripts)
        {
            var source = File.ReadAllText(script);
            source.Should().NotContain("gdal.Open(", $"{Path.GetFileName(script)} must use gdal.OpenEx with allowed_drivers");
            if (source.Contains("gdal.OpenEx(", StringComparison.Ordinal))
            {
                source.Should().Contain("allowed_drivers=", $"{Path.GetFileName(script)} must pass allowed_drivers to gdal.OpenEx");
            }
        }
    }

    private static byte[] CreateZip(params (string Name, byte[] Bytes)[] entries)
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
                using var stream = entry.Open();
                stream.Write(bytes);
            }
        }

        return memory.ToArray();
    }
}
