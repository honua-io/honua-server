// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Exports;
using Honua.Server.Features.Admin.Exports;
using Honua.Server.Features.Admin.TileOperations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.Admin;

public sealed class ExportActivationTests
{
    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SwitchPointer_FailedProviderResult_RetainsUnknownVersionOrRecoversActiveVersion(bool activated)
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateFailure("A transport failure does not prove rejection."));
        var run = new ExportVersionRun("test", "version", "capture", DateTimeOffset.UtcNow);
        var pointer = new ExportPointer(run.PublicationId, run.Version, "version/collection.json", DateTimeOffset.UtcNow);
        storage.DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<Stream?>(activated
                ? new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(pointer, ExportVersionJsonContext.Default.ExportPointer))
                : null));
        var store = ExportStore.Create(storage, new CloudStorageOptions(), run.PublicationId);

        var activate = () => TileOperationExecutionCore.SwitchPointerAsync(store, run, expectedETag: null);

        if (activated)
        {
            await activate.Should().NotThrowAsync();
        }
        else
        {
            await activate.Should().ThrowAsync<ExportActivationUnknownException>();
        }
        await storage.DidNotReceive().DeleteAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task BeginOperation_UncertainReservation_DoesNotAuthorizeArtifactWrites()
    {
        var storage = Substitute.For<ICloudFileStorage>();
        storage.UploadIfMatchAsync(Arg.Any<FileUploadRequest>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(UploadResult.CreateFailure("Transport failure"));
        var store = ExportStore.Create(storage, new CloudStorageOptions(), "test");
        var run = new ExportVersionRun("test", "version", "capture", DateTimeOffset.UtcNow);

        var reserve = () => store.BeginOperationAsync("operation", run, CancellationToken.None);

        await reserve.Should().ThrowAsync<ExportActivationUnknownException>();
        await storage.DidNotReceive().UploadAsync(Arg.Any<FileUploadRequest>(), Arg.Any<CancellationToken>());
    }
}
