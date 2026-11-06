// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Infrastructure.FileStorage;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.FileStorage;

public sealed class ArtifactRevisionReadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public async Task ArtifactRead_GenerationChangesAtByteAcquisition_ReturnsRevisionChanged(bool range)
    {
        var storage = Substitute.For<ICloudFileStorage>();
        var metadata = Metadata();
        storage.DownloadIfMatchAsync(metadata.FileId, metadata.ETag!, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Stream?>(new CloudFileRevisionChangedException()));
        var reader = new ArtifactByteReader(storage, [], [], Options.Create(new CloudStorageOptions()));

        var result = range
            ? await reader.ReadRangeAsync(metadata, "bytes=0-6", CancellationToken.None)
            : await reader.ReadFullAsync(metadata, CancellationToken.None);

        result.Outcome.Should().Be(ArtifactRangeOutcome.RevisionChanged);
        result.Payload.Should().BeNull();
        result.Stream.Should().BeNull();
        await storage.DidNotReceive().DownloadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Tier", "Fast")]
    public async Task ArtifactRead_ProviderCannotFenceGeneration_ReturnsUnsupported(bool range)
    {
        var storage = Substitute.For<ICloudFileStorage>();
        var metadata = Metadata();
        storage.DownloadIfMatchAsync(metadata.FileId, metadata.ETag!, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Stream?>(new NotSupportedException()));
        var reader = new ArtifactByteReader(storage, [], [], Options.Create(new CloudStorageOptions()));

        var result = range
            ? await reader.ReadRangeAsync(metadata, "bytes=0-6", CancellationToken.None)
            : await reader.ReadFullAsync(metadata, CancellationToken.None);

        result.Outcome.Should().Be(ArtifactRangeOutcome.Unsupported);
        result.Payload.Should().BeNull();
        result.Stream.Should().BeNull();
    }

    [UnitTest]
    public void CreateEntityTag_SameSizeAndSecondDifferentRevision_ChangesStrongValidator()
    {
        var metadata = Metadata();
        var original = ArtifactHttpConditions.CreateEntityTag(metadata);
        var replacement = ArtifactHttpConditions.CreateEntityTag(metadata with { ETag = "replacement" });
        original.IsWeak.Should().BeFalse();
        replacement.IsWeak.Should().BeFalse();
        replacement.ToString().Should().NotBe(original.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("legacy-000001-000010")]
    [InlineData("W/\"unproven\"")]
    [Trait("Tier", "Fast")]
    public void CreateEntityTag_UnprovenGeneration_UsesWeakValidator(string? revision)
    {
        ArtifactHttpConditions.CreateEntityTag(Metadata() with { ETag = revision }).IsWeak.Should().BeTrue();
    }

    private static CloudFile Metadata() => new()
    {
        FileId = "published/artifact.bin",
        FileName = "artifact.bin",
        StoragePath = "published/artifact.bin",
        ContentType = "application/octet-stream",
        SizeBytes = 16,
        UploadedAt = DateTimeOffset.Parse("2026-10-27T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        ETag = "original",
        Provider = CloudStorageProvider.Local
    };
}
