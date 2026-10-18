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
            .AddSingleton(importer).BuildServiceProvider();
        var executor = new FileImportJobExecutor(services.GetRequiredService<IServiceScopeFactory>(),
            Substitute.For<IUniversalProgressStore>(), NullLogger<FileImportJobExecutor>.Instance);
        var parameters = FileImportJobParameters.Build(Request(receipt), receipt.Bytes);
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = Guid.NewGuid().ToString("N"), Status = ExecutionJobStatus.Running,
            CreatedAt = now, UpdatedAt = now,
            Spec = new ExecutionJobSpec { Kind = ExecutionJobKind.FileImport, TargetKind = BatchComputeTargetKind.AwsBatch,
                Backend = "in-process", WorkloadName = "file-import", Parameters = parameters }
        };

        var result = await executor.ExecuteAsync(job, Substitute.For<IJobExecutionContext>(), CancellationToken.None);

        result.Status.Should().Be(scenario == "unchanged" ? ExecutionJobStatus.Succeeded : ExecutionJobStatus.Failed);
        if (scenario == "unchanged")
        {
            imported.Should().NotBeNull();
            imported!.CanRead.Should().BeFalse("the worker must dispose private scratch after import");
            result.FileImportOutput!.PhysicalTableName.Should().Be("roads");
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

        await FluentActions.Invoking(() => StagedImportSourceMaterializer.OpenAsync(source, storage, source.Bytes))
            .Should().ThrowAsync<InvalidDataException>();

        content.CanRead.Should().BeFalse();
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

        await FluentActions.Invoking(() => StagedImportSourceMaterializer.OpenAsync(source, storage, source.Bytes))
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
        CloudFileId = source.FileId, StagedSource = source, FileName = "roads.geojson", TableName = "roads"
    };

    private sealed class Fixture : IDisposable
    {
        private static readonly byte[] Content = "{\"type\":\"FeatureCollection\",\"features\":[]}"u8.ToArray();
        public string Root { get; } = Path.Join(Path.GetTempPath(), "honua-source-binding-" + Guid.NewGuid().ToString("N"));
        public LocalFileStorage Storage { get; }
        public Fixture() => Storage = new LocalFileStorage(
            Options.Create(new LocalStorageOptions { BasePath = Root, CreateDirectoryIfNotExists = true }),
            NullLogger<LocalFileStorage>.Instance, Substitute.For<IUploadProgressStore>());
        public async Task<ImportStagedSourceBinding> StageAsync()
        {
            await using var content = new MemoryStream(Content, writable: false);
            var result = await Storage.UploadAsync(new FileUploadRequest { Content = content, FileName = "roads.geojson",
                ContentType = "application/geo+json", SizeBytes = Content.Length, ObjectKeyOverride = "imports/pinned-source" });
            result.Success.Should().BeTrue();
            return new(result.File!.FileId, result.File.ETag!, Content.Length, Convert.ToHexStringLower(SHA256.HashData(Content)));
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
