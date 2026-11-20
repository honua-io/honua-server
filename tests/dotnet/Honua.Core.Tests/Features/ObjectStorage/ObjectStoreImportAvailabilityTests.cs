// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data.Common;
using FluentAssertions;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;
using NSubstitute;

namespace Honua.Core.Tests.Features.ObjectStorage;

public sealed class ObjectStoreImportAvailabilityTests
{
    private static readonly ObjectStorageConnection Connection = new()
    {
        Id = Guid.Parse("4b0b7c2e-91a3-4f0e-b3a1-2a7d4c9e5f10"),
        Name = "Customer",
        Bucket = "customer-imports",
        Region = "us-west-2",
    };

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ImportIsConfigured_OnlyWithAReaderAndAConnection()
    {
        var reader = Substitute.For<IImportObjectStoreReader>();

        (await new ObjectStoreImportAvailability(Resolver(Connection)).IsConfiguredAsync(CancellationToken.None))
            .Should().BeFalse("this host cannot read S3");
        (await new ObjectStoreImportAvailability(Resolver(), reader).IsConfiguredAsync(CancellationToken.None))
            .Should().BeFalse("no connection exists, so nothing was opted in");
        (await new ObjectStoreImportAvailability(Resolver(Connection), reader).IsConfiguredAsync(CancellationToken.None))
            .Should().BeTrue();
    }

    [Trait("Tier", "Fast")]
    [Fact]
    public async Task ImportIsUnconfigured_WhenTheConnectionTableCannotBeRead()
    {
        var resolver = Substitute.For<IObjectStorageConnectionResolver>();
        resolver.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IReadOnlyList<ObjectStorageConnection>>(new TestDbException()));

        (await new ObjectStoreImportAvailability(resolver, Substitute.For<IImportObjectStoreReader>()).IsConfiguredAsync(CancellationToken.None))
            .Should().BeFalse();
    }

    private static IObjectStorageConnectionResolver Resolver(params ObjectStorageConnection[] connections)
    {
        var resolver = Substitute.For<IObjectStorageConnectionResolver>();
        resolver.ListAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ObjectStorageConnection>>(connections));
        return resolver;
    }

    private sealed class TestDbException : DbException;
}
