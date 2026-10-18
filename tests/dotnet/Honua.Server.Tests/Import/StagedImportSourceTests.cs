// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.FileStorage;
using Honua.Import.FileImport.Durable;
using Honua.Import.FileImport.Resumable;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Import;

public sealed class StagedImportSourceTests
{
    [IntegrationTheory]
    [InlineData("unchanged")]
    [InlineData("cleanup-failed")]
    [InlineData("budget-exhausted")]
    [InlineData("replaced")]
    [InlineData("corrupt")]
    public async Task Worker_UsesOnlyExactVerifiedSourceGeneration(string scenario)
    {
        using var fixture = new Fixture();
        var receipt = await fixture.StageAsync();
        if (scenario == "replaced") _ = await fixture.StageAsync(); // Same bytes/key, different generation.
        if (scenario == "corrupt")
        {
            var metadata = (await fixture.Storage.GetMetadataAsync(receipt.FileId))!;
            await File.WriteAllBytesAsync(Path.Join(fixture.Root, metadata.StoragePath), new byte[receipt.Bytes]);
        }

        if (scenario == "cleanup-failed") fixture.Budget.ReleaseDeletedAsync(Arg.Any<ImportStorageReservation>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<bool>(new IOException("Cleanup response lost")));
        if (scenario == "budget-exhausted") fixture.Budget.TryReserveAsync(Arg.Any<ImportStorageReservation>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var importer = Substitute.For<IFileImportService>();
        importer.Limits.Returns(new ImportLimits { MaxWorkerScratchBytes = receipt.Bytes });
        Stream? imported = null;
        importer.ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<IProgress<ImportProgress>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var request = call.ArgAt<ImportRequest>(0);
                request.CloudFileId.Should().BeNull();
                request.StagedSource.Should().Be(receipt);
                imported = request.FileStream!;
                imported.CanSeek.Should().BeTrue();
                imported.Position.Should().Be(0);
                Convert.ToHexStringLower(await SHA256.HashDataAsync(imported)).Should().Be(receipt.Sha256);
                return new ImportResult { Success = true, TableName = "roads", PhysicalTableName = "roads",
                    Schema = "honua_data", FeatureCount = 1, Format = SupportedFileFormat.GeoJson };
            });
        using var services = new ServiceCollection().AddSingleton<ICloudFileStorage>(fixture.Storage)
            .AddSingleton(importer).AddSingleton(fixture.Scratch).BuildServiceProvider();
        var executor = new FileImportJobExecutor(services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IUniversalProgressStore>(), NullLogger<FileImportJobExecutor>.Instance);
        var acceptedRequest = Request(receipt);
        var parameters = FileImportJobParameters.Build(acceptedRequest, receipt.Bytes);
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = acceptedRequest.Acceptance!.OperationId, Status = ExecutionJobStatus.Running,
            CreatedAt = now, UpdatedAt = now,
            Spec = new ExecutionJobSpec { Kind = ExecutionJobKind.FileImport, TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "in-process", WorkloadName = "file-import", Parameters = parameters }
        };

        var result = await executor.ExecuteAsync(job, Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(scenario is "unchanged" or "cleanup-failed" ? ExecutionJobStatus.Succeeded : ExecutionJobStatus.Failed);
        if (scenario is "unchanged" or "cleanup-failed")
        {
            imported.Should().NotBeNull();
            imported!.CanRead.Should().BeFalse("the worker must dispose private scratch after import");
            result.FileImportOutput!.PhysicalTableName.Should().Be("roads");
            await importer.Received(1).ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<IProgress<ImportProgress>>(), Arg.Any<CancellationToken>());
        }
        else
        {
            await importer.DidNotReceive().ImportFileAsync(Arg.Any<ImportRequest>(), Arg.Any<IProgress<ImportProgress>>(), Arg.Any<CancellationToken>());
        }
    }

    [IntegrationTest]
    public async Task Materialize_AllowanceRefusedBeforeAnyProviderRead()
    {
        using var fixture = new Fixture();
        var source = await fixture.StageAsync();
        var inaccessible = Substitute.For<ICloudFileStorage>();
        await FluentActions.Invoking(() => StagedImportSourceMaterializer.OpenAsync(source, inaccessible, source.Bytes - 1))
            .Should().ThrowAsync<InvalidOperationException>();
        inaccessible.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Materialize_MissingBudgetOrProvenance_DoesNotReadProvider(bool missingProvenance)
    {
        using var fixture = new Fixture();
        var source = await fixture.StageAsync();
        if (missingProvenance) source = source with { Transfer = null };
        var inaccessible = Substitute.For<ICloudFileStorage>();
        await FluentActions.Invoking(() => StagedImportSourceMaterializer.OpenAsync(source, inaccessible,
            source.Bytes, missingProvenance ? fixture.Scratch : null)).Should().ThrowAsync<InvalidOperationException>();
        inaccessible.ReceivedCalls().Should().BeEmpty();
        await fixture.Budget.DidNotReceive().TryReserveAsync(Arg.Any<ImportStorageReservation>(), Arg.Any<CancellationToken>());
    }

    [UnitTheory]
    [InlineData("owner")]
    [InlineData("tenant")]
    [InlineData("intent")]
    [InlineData("source-key")]
    [InlineData("missing-acceptance")]
    [InlineData("null-provenance")]
    [InlineData("oversized-provenance")]
    [InlineData("unknown-property")]
    [InlineData("source-removed")]
    public void Parameters_TransferProvenanceSurvivesQueueAndCannotChangeAccountingScope(string mutation)
    {
        var transfer = new ImportTransferProvenance(Guid.NewGuid().ToString("N"), "executor", "tenant", "target", "workspace",
            "task", 3, new string('b', 64));
        var source = new ImportStagedSourceBinding($"imports/resumable/{transfer.UploadId}/source-{Guid.NewGuid():N}",
            "generation", 5L * 1024 * 1024 * 1024, new string('a', 64), Transfer: transfer);
        var parameters = FileImportJobParameters.Build(Request(source), source.Bytes);
        FileImportJobParameters.TryParse(parameters, out var restored, out var bytes, out _).Should().BeTrue();
        restored!.StagedSource.Should().Be(source);
        bytes.Should().Be(source.Bytes); // Metadata only; not a multi-GB transfer qualification.

        switch (mutation)
        {
            case "owner": parameters[FileImportJobParameters.OwnerActorId] = "different-executor"; break;
            case "tenant": parameters[FileImportJobParameters.TenantId] = "different-tenant"; break;
            case "intent": parameters[FileImportJobParameters.AcceptanceIntent] = new string('c', 64); break;
            case "source-key": parameters[FileImportJobParameters.CloudFileId] = $"imports/resumable/{Guid.NewGuid():N}/source-{Guid.NewGuid():N}"; break;
            case "missing-acceptance":
                parameters.Remove(FileImportJobParameters.AcceptanceId);
                parameters.Remove(FileImportJobParameters.AcceptanceIntent);
                parameters.Remove(FileImportJobParameters.AcceptanceDeadline);
                break;
            case "null-provenance": parameters[FileImportJobParameters.StagedTransfer] = "null"; break;
            case "oversized-provenance": parameters[FileImportJobParameters.StagedTransfer] = new string('x', 16385); break;
            case "unknown-property": parameters[FileImportJobParameters.StagedTransfer] = parameters[FileImportJobParameters.StagedTransfer][..^1] + ",\"authority\":true}"; break;
            case "source-removed":
                parameters.Remove(FileImportJobParameters.StagedSourceETag);
                parameters.Remove(FileImportJobParameters.StagedSourceSha256);
                break;
        }

        FileImportJobParameters.TryParse(parameters, out _, out _, out _).Should().BeFalse();
    }

    [IntegrationTheory]
    [InlineData(-1)]
    [InlineData(1)]
    public async Task Materialize_ActualLengthDiffersFromMetadata_RejectsSource(int delta)
    {
        using var fixture = new Fixture();
        var source = await fixture.StageAsync();
        var metadata = (await fixture.Storage.GetMetadataAsync(source.FileId))!;
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(source.FileId, Arg.Any<CancellationToken>()).Returns(metadata);
        var content = new MemoryStream(new byte[source.Bytes + delta], writable: false);
        storage.DownloadAsync(source.FileId, Arg.Any<CancellationToken>()).Returns(content);

        await FluentActions.Invoking(() => StagedImportSourceMaterializer.OpenAsync(source, storage, source.Bytes, fixture.Scratch))
            .Should().ThrowAsync<InvalidDataException>();

        content.CanRead.Should().BeFalse();
    }

    [UnitTest]
    public async Task Materialize_ExpiredSourceIsRefusedBeforeProviderAccess()
    {
        var source = new ImportStagedSourceBinding("imports/pinned", "generation", 100, new string('a', 64),
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var storage = Substitute.For<ICloudFileStorage>();

        await FluentActions.Invoking(() => StagedImportSourceMaterializer.OpenAsync(source, storage, source.Bytes))
            .Should().ThrowAsync<InvalidDataException>();

        storage.ReceivedCalls().Should().BeEmpty();
    }

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Materialize_RetentionChangesBeforeOrDuringDownload_RejectsSource(bool duringDownload)
    {
        using var fixture = new Fixture();
        var source = await fixture.StageAsync();
        var metadata = (await fixture.Storage.GetMetadataAsync(source.FileId))!;
        var changed = metadata with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(7) };
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(source.FileId, Arg.Any<CancellationToken>())
            .Returns(duringDownload ? metadata : changed, changed);
        Stream? downloaded = null;
        storage.DownloadAsync(source.FileId, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            downloaded = await fixture.Storage.DownloadAsync(source.FileId, call.ArgAt<CancellationToken>(1));
            return downloaded;
        });

        await FluentActions.Invoking(() => StagedImportSourceMaterializer.OpenAsync(source, storage, source.Bytes, fixture.Scratch))
            .Should().ThrowAsync<InvalidDataException>();

        if (duringDownload)
        {
            downloaded.Should().NotBeNull();
            downloaded!.CanRead.Should().BeFalse();
        }
        else
        {
            await storage.DidNotReceive().DownloadAsync(source.FileId, Arg.Any<CancellationToken>());
        }
    }

    [UnitTheory]
    [InlineData("")]
    [InlineData("invalid")]
    [InlineData("2030-01-01")]
    public void Parameters_InvalidRetentionCannotDowngradeToPermanentSource(string invalidExpiry)
    {
        var source = new ImportStagedSourceBinding("imports/pinned", "generation", 100, new string('a', 64),
            DateTimeOffset.UtcNow.AddDays(7));
        var parameters = FileImportJobParameters.Build(Request(source), source.Bytes);
        FileImportJobParameters.TryParse(parameters, out var restored, out _, out _).Should().BeTrue();
        restored!.StagedSource.Should().Be(source);

        parameters[FileImportJobParameters.StagedSourceExpiresAt] = invalidExpiry;

        FileImportJobParameters.TryParse(parameters, out _, out _, out _).Should().BeFalse();
    }

    [IntegrationTest]
    public async Task Materialize_GenerationChangesDuringDownload_RejectsSameContentReplacement()
    {
        using var fixture = new Fixture();
        var source = await fixture.StageAsync();
        var storage = Substitute.For<ICloudFileStorage>();
        storage.GetMetadataAsync(source.FileId, Arg.Any<CancellationToken>())
            .Returns(call => fixture.Storage.GetMetadataAsync(source.FileId, call.ArgAt<CancellationToken>(1)));
        storage.DownloadAsync(source.FileId, Arg.Any<CancellationToken>()).Returns(async call =>
        {
            _ = await fixture.StageAsync();
            return await fixture.Storage.DownloadAsync(source.FileId, call.ArgAt<CancellationToken>(1));
        });

        await FluentActions.Invoking(() => StagedImportSourceMaterializer.OpenAsync(source, storage, source.Bytes, fixture.Scratch))
            .Should().ThrowAsync<InvalidDataException>();
    }

    [UnitTheory]
    [InlineData("etag")]
    [InlineData("digest")]
    [InlineData("bytes")]
    [InlineData("mixed")]
    public void Parameters_PartialOrAmbiguousSourceBindingIsNeverDowngraded(string mutation)
    {
        var source = new ImportStagedSourceBinding("imports/pinned", "generation", 5L * 1024 * 1024 * 1024, new string('a', 64));
        var parameters = FileImportJobParameters.Build(Request(source), source.Bytes);
        FileImportJobParameters.TryParse(parameters, out var restored, out var size, out _).Should().BeTrue();
        restored!.StagedSource.Should().Be(source);
        size.Should().Be(source.Bytes); // 64-bit metadata roundtrip, not a five-GB transfer.
        switch (mutation)
        {
            case "etag": parameters.Remove(FileImportJobParameters.StagedSourceETag); break;
            case "digest": parameters[FileImportJobParameters.StagedSourceSha256] = "not-a-digest"; break;
            case "bytes": parameters[FileImportJobParameters.FileSizeBytes] = "-1"; break;
            case "mixed": parameters[FileImportJobParameters.ObjectStoreUri] = "s3://bucket/object";
                parameters[FileImportJobParameters.ObjectStoreETag] = "another-generation"; break;
        }
        FileImportJobParameters.TryParse(parameters, out _, out _, out _).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parameters_SourceKeyOrLengthMismatchCannotBeAccepted(bool changeKey)
    {
        var source = new ImportStagedSourceBinding("imports/pinned", "generation", 100, new string('a', 64));
        var request = Request(source);
        if (changeKey) request = request with { CloudFileId = "imports/replacement" };
        FluentActions.Invoking(() => FileImportJobParameters.Build(request, changeKey ? source.Bytes : source.Bytes + 1))
            .Should().Throw<InvalidOperationException>();
    }

    private static ImportRequest Request(ImportStagedSourceBinding source) => new()
    {
        CloudFileId = source.FileId, StagedSource = source, FileName = "roads.geojson", TableName = "roads",
        Ownership = source.Transfer is { } transfer ? new(transfer.OwnerActorId, transfer.TenantId) : null,
        Acceptance = source.Transfer is { } origin ? new(Guid.NewGuid().ToString("N"), origin.TaskContentHash, DateTimeOffset.UtcNow.AddHours(1)) : null
    };

    private sealed class Fixture : IDisposable
    {
        private static readonly byte[] Content = "{\"type\":\"FeatureCollection\",\"features\":[]}"u8.ToArray();
        public string Root { get; } = Path.Join(Path.GetTempPath(), "honua-source-binding-" + Guid.NewGuid().ToString("N"));
        public LocalFileStorage Storage { get; }
        public IImportStorageBudget Budget { get; } = Substitute.For<IImportStorageBudget>();
        public ImportScratchStorage Scratch { get; }
        public ImportTransferProvenance Transfer { get; } = new(Guid.NewGuid().ToString("N"), "executor", "tenant", "target", "workspace", "task", 1, new string('b', 64));
        private readonly string _sourceId = Guid.NewGuid().ToString("N");
        public Fixture()
        {
            Budget.TryReserveAsync(Arg.Any<ImportStorageReservation>(), Arg.Any<CancellationToken>()).Returns(true);
            Budget.ReleaseDeletedAsync(Arg.Any<ImportStorageReservation>(), Arg.Any<CancellationToken>()).Returns(true);
            Scratch = new ImportScratchStorage(Path.Join(Root, "scratch"), Budget);
            Storage = new LocalFileStorage(
                Options.Create(new LocalStorageOptions { BasePath = Root, CreateDirectoryIfNotExists = true }),
                NullLogger<LocalFileStorage>.Instance, Substitute.For<IUploadProgressStore>());
        }
        public async Task<ImportStagedSourceBinding> StageAsync()
        {
            await using var content = new MemoryStream(Content, writable: false);
            var result = await Storage.UploadAsync(new FileUploadRequest { Content = content, FileName = "roads.geojson",
                ContentType = "application/geo+json", SizeBytes = Content.Length, ObjectKeyOverride = $"imports/resumable/{Transfer.UploadId}/source-{_sourceId}" });
            result.Success.Should().BeTrue();
            return new(result.File!.FileId, result.File.ETag!, Content.Length, Convert.ToHexStringLower(SHA256.HashData(Content)), Transfer: Transfer);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
