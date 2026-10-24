// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Geoprocessing;
using Honua.Geoprocessing.Execution;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NSubstitute;

namespace Honua.Geoprocessing.Tests;

public sealed class ServerJobArtifactImporterTests
{
    // EWKB POINT(0 0) with embedded SRID 3857.
    private const string PointEwkbSrid3857Base64 = "AQEAACARDwAAAAAAAAAAAAAAAAAAAAAAAA==";

    [UnitTest]
    public async Task Buffer3857_ImportsByReference_AndDoesNotRelabelTheProducerCrs()
    {
        var artifact = await BufferArtifactAsync(srid: "3857", distance: "10");
        var imports = Substitute.For<IFileImportService>();
        imports.Limits.Returns(new ImportLimits());
        byte[]? stored = null;
        ImportRequest? request = null;
        imports.ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                request = call.Arg<ImportRequest>();
                request.LocalFilePath.Should().BeNull();
                request.SourceUrl.Should().BeNull();
                request.ObjectStoreUri.Should().BeNull();
                request.FileStream!.Position = 0;
                using var copy = new MemoryStream();
                request.FileStream.CopyTo(copy);
                stored = copy.ToArray();
                return ImportResult.CreateSuccess(
                    request.TableName, SupportedFileFormat.GeoJson, 1, 3857,
                    physicalTableName: "imported_buffer", schema: request.TargetSchema);
            });

        var receipt = await ImportAsync(imports, artifact, targetSrid: 4326);

        receipt.SourceSrid.Should().Be(3857);
        receipt.TargetSrid.Should().Be(4326);
        receipt.CrsState.Should().Be("known");
        request!.SourceKind.Should().Be(DatasetImportSourceKinds.ServerJobArtifact);
        request.FileName.Should().Be(ServerJobArtifactImporter.ImportFileName);
        request.SourceSrid.Should().Be(3857, "a projected buffer must not be stored as if it were RFC 7946 longitude/latitude");
        request.TargetSrid.Should().Be(4326);
        using var document = JsonDocument.Parse(stored);
        var crs = document.RootElement.GetProperty("honua:spatialReference");
        crs.GetProperty("state").GetString().Should().Be("known");
        crs.GetProperty("srid").GetInt32().Should().Be(3857);
        Coordinates(document.RootElement).Should().Contain(value => Math.Abs(value) > 1);
        await imports.Received(1).ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task KnownProducerCrs_IgnoresAConflictingFeatureProperty_AndRefusesRelabeling()
    {
        var artifact = DataUri(
            """
            {"type":"Feature","honua:spatialReference":{"schemaVersion":"honua.feature-crs/v1","state":"known","srid":3857},"geometry":{"type":"Point","coordinates":[500000,0]},"properties":{"inputSrid":4326,"srid":4326}}
            """);
        var recording = new RecordingImports();
        var receipt = await ImportAsync(recording.Service, artifact, targetSrid: 4326);

        receipt.SourceSrid.Should().Be(3857);
        recording.Request!.SourceSrid.Should().Be(3857);
        using var document = JsonDocument.Parse(recording.Stored);
        document.RootElement.GetProperty("features")[0].GetProperty("geometry").GetProperty("coordinates")[0].GetDouble()
            .Should().Be(500000);

        var relabel = () => ImportAsync(recording.Service, artifact, targetSrid: 4326, sourceSrid: 4326, intentId: "other-intent");
        await relabel.Should().ThrowAsync<GeoprocessingValidationException>();
    }

    [UnitTest]
    public async Task UnknownProducerCrs_RequiresAnExplicitSourceSrid_AndDoesNotTrustAProperty()
    {
        var artifact = DataUri(
            """
            {"type":"Feature","geometry":{"type":"Point","coordinates":[500000,0]},"properties":{"inputSrid":4326}}
            """);
        var recording = new RecordingImports();
        var missing = () => ImportAsync(recording.Service, artifact, targetSrid: 3857);
        await missing.Should().ThrowAsync<GeoprocessingValidationException>();
        await recording.Service.DidNotReceive().ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());

        var receipt = await ImportAsync(recording.Service, artifact, targetSrid: 3857, sourceSrid: 3857, intentId: "chosen-source");
        receipt.SourceSrid.Should().Be(3857);
        receipt.CrsState.Should().Be("unknown");
        recording.Request!.SourceSrid.Should().Be(3857);
    }

    [UnitTest]
    public async Task ExactIntentReplay_ImportsOnce_AndADifferentCommandConflicts()
    {
        var artifact = await BufferArtifactAsync("3857", "10");
        var recording = new RecordingImports();
        var ledger = new MemoryServerJobArtifactImportLedger();
        var first = await ImportAsync(recording.Service, artifact, 4326, ledger: ledger);
        var second = await ImportAsync(recording.Service, artifact, 4326, ledger: ledger);
        second.Should().BeSameAs(first);
        await recording.Service.Received(1).ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());

        var conflict = () => ImportAsync(recording.Service, artifact, 4326, ledger: ledger, table: "other_table");
        await conflict.Should().ThrowAsync<GeoprocessingIdempotencyConflictException>();
        await recording.Service.Received(1).ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SameActorAndIntent_InAnotherTenant_ImportsAgain()
    {
        var artifact = await BufferArtifactAsync("3857", "10");
        var recording = new RecordingImports();
        var ledger = new MemoryServerJobArtifactImportLedger();
        var first = await ImportAsync(recording.Service, artifact, 4326, ledger: ledger, tenantId: "tenant-a");
        var second = await ImportAsync(recording.Service, artifact, 4326, ledger: ledger, tenantId: "tenant-b");
        second.Should().NotBeSameAs(first);
        await recording.Service.Received(2).ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());

        var replay = await ImportAsync(recording.Service, artifact, 4326, ledger: ledger, tenantId: "tenant-a");
        replay.Should().BeSameAs(first);
        await recording.Service.Received(2).ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public void VerifiedSrid_PlusUntaggedGeometry_IsMixed_InEitherOrder()
    {
        var known = new Point(1, 2) { SRID = 3857 };
        var untagged = new Point(3, 4) { SRID = 4326 };

        var knownFirst = new FeatureArtifactSpatialReferenceAccumulator();
        knownFirst.Add(known, hasProducerSpatialReference: true);
        knownFirst.Add(untagged, hasProducerSpatialReference: false);
        knownFirst.Value.State.Should().Be("mixed");
        knownFirst.Value.Srid.Should().BeNull();

        var untaggedFirst = new FeatureArtifactSpatialReferenceAccumulator();
        untaggedFirst.Add(untagged, hasProducerSpatialReference: false);
        untaggedFirst.Add(known, hasProducerSpatialReference: true);
        untaggedFirst.Value.State.Should().Be("mixed");
        untaggedFirst.Value.Srid.Should().BeNull();

        var bothUntagged = new FeatureArtifactSpatialReferenceAccumulator();
        bothUntagged.Add(untagged, hasProducerSpatialReference: false);
        bothUntagged.Add(new Point(5, 6) { SRID = 32610 }, hasProducerSpatialReference: false);
        bothUntagged.Value.State.Should().Be("unknown");
        bothUntagged.Value.Srid.Should().BeNull();

        var same = new FeatureArtifactSpatialReferenceAccumulator();
        same.Add(known, hasProducerSpatialReference: true);
        same.Add(new Point(8, 9) { SRID = 3857 }, hasProducerSpatialReference: true);
        same.Value.State.Should().Be("known");
        same.Value.Srid.Should().Be(3857);
    }

    [UnitTest]
    public void ScratchPath_StaysUnderTheDirectory_AndRejectsARootedSegment()
    {
        var directory = Path.Join(Path.GetTempPath(), "honua-scratch-root");
        var path = ServerJobArtifactImporter.CreateScratchPath(directory, "job-artifact-abc");
        var root = Path.GetFullPath(directory);
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        path.Should().Be(Path.GetFullPath(Path.Join(root, "job-artifact-abc")));
        path.StartsWith(prefix, StringComparison.Ordinal).Should().BeTrue();

        var rooted = Path.DirectorySeparatorChar + "job-artifact-abc";
        var act = () => ServerJobArtifactImporter.CreateScratchPath(directory, rooted);
        act.Should().Throw<InvalidOperationException>();
    }

    [UnitTheory]
    [InlineData("failed")]
    [InlineData("package")]
    [InlineData("artifact")]
    [InlineData("hash")]
    [InlineData("uri")]
    [InlineData("path")]
    public async Task RefusesBeforeImport(string fault)
    {
        var artifact = DataUri("""{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{}}""");
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var job = CompletedJob(artifact.Uri, version: 2);
        if (fault == "failed")
        {
            job = job with { Status = ExecutionJobStatus.Failed };
        }

        jobs.GetJobAsync(job.OperationId, Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(job);
        var imports = new RecordingImports().Service;
        var reference = Reference(artifact, version: fault == "package" ? 9 : 2, ordinal: fault == "artifact" ? 2 : 1);
        if (fault == "hash")
        {
            reference = reference with { ReferenceSha256 = new string('b', 64) };
        }

        if (fault == "uri")
        {
            job = CompletedJob("https://untrusted.example/output.geojson", version: 2);
            jobs.GetJobAsync(job.OperationId, Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(job);
            reference = ReferenceFor(job, job.ArtifactReferences[0]);
        }

        if (fault == "path")
        {
            job = CompletedJob("/tmp/worker/output.geojson", version: 2);
            jobs.GetJobAsync(job.OperationId, Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(job);
            reference = ReferenceFor(job, job.ArtifactReferences[0]);
        }

        var act = () => ServerJobArtifactImporter.ImportAsync(
            Command(reference), Principal(), jobs, imports, new MemoryServerJobArtifactImportLedger(),
            new ServiceCollection().BuildServiceProvider(), CancellationToken.None);
        await act.Should().ThrowAsync<Exception>();
        await imports.DidNotReceive().ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ChangedArtifact_RefusesBeforeImport()
    {
        var artifact = DataUri("""{"type":"Feature","geometry":{"type":"Point","coordinates":[1,2]},"properties":{}}""");
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var original = CompletedJob(artifact.Uri, version: 2);
        var changed = original with { ArtifactReferences = [DataUri("""{"type":"Feature","geometry":{"type":"Point","coordinates":[9,9]},"properties":{}}""").Uri] };
        jobs.GetJobAsync(original.OperationId, Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>())
            .Returns(original, changed);
        var imports = new RecordingImports().Service;
        var act = () => ServerJobArtifactImporter.ImportAsync(
            Command(Reference(artifact, 2, 1)), Principal(), jobs, imports, new MemoryServerJobArtifactImportLedger(),
            new ServiceCollection().BuildServiceProvider(), CancellationToken.None);
        await act.Should().ThrowAsync<GeoprocessingPreconditionFailedException>();
        await imports.DidNotReceive().ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("browser-file")]
    [InlineData("local-process-artifact")]
    [InlineData("https://untrusted.example/file.geojson")]
    public void OtherSourceKindsAreRecognizedAndAreNotJobArtifacts(string kind)
    {
        var act = () => DatasetImportSourceRules.RequireServerJobArtifact(kind);
        act.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void ServerJobArtifactKindIsTheAdmittedVariant()
        => DatasetImportSourceRules.RequireServerJobArtifact(DatasetImportSourceKinds.ServerJobArtifact);

    private static async Task<ServerJobArtifactImportReceipt> ImportAsync(
        IFileImportService imports,
        Artifact artifact,
        int targetSrid,
        int? sourceSrid = null,
        string intentId = "intent-buffer",
        string table = "buffered",
        string tenantId = "tenant-a",
        IServerJobArtifactImportLedger? ledger = null)
    {
        var jobs = Substitute.For<IGeoprocessingJobService>();
        var job = CompletedJob(artifact.Uri, version: 2);
        jobs.GetJobAsync(job.OperationId, Arg.Any<ClaimsPrincipal>(), Arg.Any<CancellationToken>()).Returns(job);
        return await ServerJobArtifactImporter.ImportAsync(
            Command(Reference(artifact, 2, 1), intentId, table, targetSrid, sourceSrid, tenantId),
            Principal(),
            jobs,
            imports,
            ledger ?? new MemoryServerJobArtifactImportLedger(),
            new ServiceCollection().BuildServiceProvider(),
            CancellationToken.None);
    }

    private sealed class RecordingImports
    {
        public RecordingImports()
        {
            Service = Substitute.For<IFileImportService>();
            Service.Limits.Returns(new ImportLimits());
            Service.ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<CancellationToken>())
                .Returns(call =>
                {
                    Request = call.Arg<ImportRequest>();
                    Request.FileStream!.Position = 0;
                    using var copy = new MemoryStream();
                    Request.FileStream.CopyTo(copy);
                    Stored = copy.ToArray();
                    return ImportResult.CreateSuccess(
                        Request.TableName,
                        SupportedFileFormat.GeoJson,
                        1,
                        Request.SourceSrid,
                        physicalTableName: "imported_" + Request.TableName,
                        schema: Request.TargetSchema);
                });
        }

        public IFileImportService Service { get; }
        public ImportRequest? Request { get; private set; }
        public byte[]? Stored { get; private set; }
    }

    private static async Task<Artifact> BufferArtifactAsync(string srid, string distance)
    {
        var options = new GeoprocessingExecutorOptions { MaxArtifactBytes = 1024 * 1024, ResultRetention = TimeSpan.FromDays(1) };
        var monitor = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        monitor.CurrentValue.Returns(options);
        var executor = new GeometryBufferJobExecutor(monitor, NullLogger<GeometryBufferJobExecutor>.Instance);
        var context = Substitute.For<IJobExecutionContext>();
        string? published = null;
        context.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                published = call.ArgAt<string>(0);
                return Task.CompletedTask;
            });
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = GeometryBufferJobExecutor.HandledProcessId,
            ["protocolProcessId"] = GeometryBufferJobExecutor.HandledProcessId,
            [$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.wkb"] = PointEwkbSrid3857Base64,
            [$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.srid"] = srid,
            [$"{ExecutionJobParameterKeys.GeoprocessingStepInputPrefix}0.distance"] = distance,
        };
        var running = new ExecutionJobRecord
        {
            OperationId = "gp-buffer",
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
        var result = await executor.ExecuteAsync(running, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded);
        published.Should().NotBeNull();
        var payload = Convert.FromBase64String(published![FeatureCollectionArtifact.DataUriPrefix.Length..]);
        using var document = JsonDocument.Parse(payload);
        document.RootElement.GetProperty("honua:spatialReference").GetProperty("srid").GetInt32().Should().Be(3857);
        return new Artifact(published);
    }

    private static Artifact DataUri(string json)
        => new(FeatureCollectionArtifact.BuildDataUri(Encoding.UTF8.GetBytes(json)));

    private static ExecutionJobRecord CompletedJob(string reference, int version) => new()
    {
        OperationId = "gp-buffer",
        Status = ExecutionJobStatus.Succeeded,
        Version = version,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
        CompletedAt = DateTimeOffset.UtcNow,
        ArtifactReferences = [reference],
        Spec = new ExecutionJobSpec
        {
            Kind = ExecutionJobKind.Geoprocessing,
            TargetKind = BatchComputeTargetKind.KubernetesJob,
            Backend = "local",
            WorkloadName = "geoprocessing:test",
            Parameters = new Dictionary<string, string>(),
        },
    };

    private static DatasetImportServerJobArtifactReference Reference(Artifact artifact, int version, int ordinal)
        => ReferenceFor(CompletedJob(artifact.Uri, version), artifact.Uri, version, ordinal);

    private static DatasetImportServerJobArtifactReference ReferenceFor(
        ExecutionJobRecord job, string reference, int version = 2, int ordinal = 1)
        => new(
            "target-local",
            job.OperationId,
            $"{job.OperationId}:v{version}",
            $"{job.OperationId}:artifact:{ordinal}",
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(reference))));

    private static ServerJobArtifactImportCommand Command(
        DatasetImportServerJobArtifactReference reference,
        string intentId = "intent-buffer",
        string table = "buffered",
        int targetSrid = 4326,
        int? sourceSrid = null,
        string tenantId = "tenant-a")
        => new(intentId, tenantId, "actor-1", "honua_data", table, reference, targetSrid, sourceSrid);

    private static ClaimsPrincipal Principal()
        => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "actor-1")], "Test"));

    private static List<double> Coordinates(JsonElement element)
    {
        var values = new List<double>();
        Walk(element, values);
        return values;
    }

    private static void Walk(JsonElement element, List<double> values, bool inCoordinates = false)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Number when inCoordinates:
                values.Add(element.GetDouble());
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    Walk(property.Value, values, inCoordinates || property.NameEquals("coordinates"));
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    Walk(item, values, inCoordinates);
                }
                break;
        }
    }

    private readonly record struct Artifact(string Uri);
}
