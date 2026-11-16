// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Admin.PairedPublications;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace Honua.Server.Tests.Features.Admin.PairedPublications;

[Collection("Unit")]
public sealed class PublicPublicationDestinationTests
{
    private static readonly PublicPublicationArtifactKey Key = new("roads", "v1", "roads.pmtiles");

    [UnitTest]
    public async Task PutAsync_ValidArtifact_UsesSeparateDestinationAndBoundedCachePolicy()
    {
        var storage = new Mock<ICloudFileStorage>(MockBehavior.Strict);
        FileUploadRequest? uploaded = null;
        storage.Setup(s => s.UploadAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()))
            .Callback<FileUploadRequest, CancellationToken>((request, _) => uploaded = request)
            .ReturnsAsync(UploadResult.CreateSuccess(new CloudFile
            {
                FileId = "data/publications/roads/v1/roads.pmtiles", FileName = "roads.pmtiles",
                StoragePath = "data/publications/roads/v1/roads.pmtiles", ContentType = "application/vnd.pmtiles",
                SizeBytes = 3, UploadedAt = DateTimeOffset.UtcNow, Provider = CloudStorageProvider.AwsS3
            }, TimeSpan.Zero));
        var destination = new PublicPublicationDestination(storage.Object, new Uri("https://cdn.example.test/"));
        using var content = new MemoryStream([1, 2, 3]);

        var uri = await destination.PutAsync(new(Key, content, "application/vnd.pmtiles"), CancellationToken.None);

        uri.AbsoluteUri.Should().Be("https://cdn.example.test/data/publications/roads/v1/roads.pmtiles");
        uploaded.Should().NotBeNull();
        uploaded!.ObjectKeyOverride.Should().Be("data/publications/roads/v1/roads.pmtiles");
        uploaded.CachePolicy.Should().Be(StoredObjectCachePolicy.PublicReplacedInPlace);
        uploaded.TimeToLive.Should().BeNull();
        uploaded.Content.Should().BeSameAs(content);
    }

    [Theory]
    [InlineData("roads", "v1", "current.json")]
    [InlineData("roads", "v1", "operations/reservation.json")]
    [InlineData("roads", "../v1", "roads.pmtiles")]
    [InlineData("../roads", "v1", "roads.pmtiles")]
    [InlineData("roads", "v1", "other.pmtiles")]
    [InlineData("roads", "v1", "roads%2fprivate.pmtiles")]
    [InlineData("roads", "v1", "../descriptor.json")]
    [Trait("Tier", "Fast")]
    public async Task PutAsync_PrivateOrEscapingKey_RefusesBeforeStorage(string publication, string version, string name)
    {
        var storage = new Mock<ICloudFileStorage>(MockBehavior.Strict);
        var destination = new PublicPublicationDestination(storage.Object, new Uri("https://cdn.example.test/"));
        using var content = new MemoryStream([1]);

        var action = () => destination.PutAsync(new(new(publication, version, name), content, "application/octet-stream"), CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentException>();
        storage.VerifyNoOtherCalls();
    }

    [UnitTest]
    public async Task PutAsync_FailedUpload_DoesNotReturnPublicUri()
    {
        var storage = new Mock<ICloudFileStorage>();
        storage.Setup(s => s.UploadAsync(It.IsAny<FileUploadRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(UploadResult.CreateFailure("unavailable"));
        var destination = new PublicPublicationDestination(storage.Object, new Uri("https://cdn.example.test/"));
        using var content = new MemoryStream([1]);

        var action = () => destination.PutAsync(new(Key, content, "application/vnd.pmtiles"), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task DeleteAsync_FailedDeletion_PropagatesFailure()
    {
        var storage = new Mock<ICloudFileStorage>();
        storage.Setup(s => s.DeleteAsync("data/publications/roads/v1/roads.pmtiles", It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var destination = new PublicPublicationDestination(storage.Object, new Uri("https://cdn.example.test/"));

        var action = () => destination.DeleteAsync(Key, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [UnitTest]
    public async Task DeleteAsync_ValidKey_DeletesOnlyGenerationArtifactAndForwardsCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var storage = new Mock<ICloudFileStorage>(MockBehavior.Strict);
        storage.Setup(s => s.DeleteAsync("data/publications/roads/v1/roads.pmtiles", cancellation.Token))
            .ReturnsAsync(true).Verifiable();
        var destination = new PublicPublicationDestination(storage.Object, new Uri("https://cdn.example.test/"));

        await destination.DeleteAsync(Key, cancellation.Token);

        storage.Verify();
        storage.VerifyNoOtherCalls();
    }

    [UnitTest]
    public async Task PutAsync_NegativeSize_RefusesBeforeStorage()
    {
        var storage = new Mock<ICloudFileStorage>(MockBehavior.Strict);
        var destination = new PublicPublicationDestination(storage.Object, new Uri("https://cdn.example.test/"));
        using var content = new MemoryStream([1]);

        var action = () => destination.PutAsync(new(Key, content, "application/vnd.pmtiles") { SizeBytes = -1 }, CancellationToken.None);

        await action.Should().ThrowAsync<ArgumentOutOfRangeException>();
        storage.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("http://cdn.example.test/")]
    [InlineData("https://user:password@cdn.example.test/")]
    [InlineData("https://cdn.example.test/private/")]
    [InlineData("https://cdn.example.test/?token=secret")]
    [InlineData("https://cdn.example.test/#fragment")]
    [Trait("Tier", "Fast")]
    public void Constructor_InvalidOrigin_RefusesConfiguration(string origin)
    {
        var action = () => new PublicPublicationDestination(Mock.Of<ICloudFileStorage>(), new Uri(origin));

        action.Should().Throw<ArgumentException>();
    }

    [UnitTest]
    public void Registration_Disabled_DoesNotExposeDestination()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();

        services.AddPublicPublicationDestination(configuration);

        services.Should().NotContain(s => s.ServiceType == typeof(IPublicPublicationDestination));
    }

#if !HONUA_EXCLUDE_AWS
    [Theory]
    [InlineData("private-files", "https://cdn.example.test/", typeof(InvalidOperationException))]
    [InlineData("public-data", "http://cdn.example.test/", typeof(ArgumentException))]
    [InlineData("public-data", "https://cdn.example.test/private/", typeof(ArgumentException))]
    [Trait("Tier", "Fast")]
    public void Registration_InvalidDestination_FailsBeforeResolvingServices(string bucket, string origin, Type exceptionType)
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["FileStorage:PublicData:Enabled"] = "true",
            ["FileStorage:PublicData:BucketName"] = bucket,
            ["FileStorage:PublicData:Region"] = "us-east-1",
            ["FileStorage:PublicData:BaseUrl"] = origin,
            ["FileStorage:AwsS3:BucketName"] = "private-files"
        }).Build();

        var exception = Record.Exception(() => services.AddPublicPublicationDestination(configuration));

        exception.Should().BeOfType(exceptionType);
        services.Should().NotContain(s => s.ServiceType == typeof(IPublicPublicationDestination));
    }
#endif
}
