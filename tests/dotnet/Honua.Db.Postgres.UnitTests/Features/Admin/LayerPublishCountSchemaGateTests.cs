// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Db.Postgres.Features.Admin;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Honua.Db.Postgres.Tests.Features.Admin;

// The admission count answers 404 for a schema the publish would refuse, before it opens a
// connection, so a queued publish is refused up front instead of failing as a job.
[global::Xunit.Trait("Tier", "Fast")]
public sealed class LayerPublishCountSchemaGateTests
{
    private const string UnreachableConnection = "Host=127.0.0.1;Port=1;Database=unused;Timeout=1";

    [Theory]
    [InlineData("v9393")]
    [InlineData(" v9393 ")]
    [InlineData("honua")]
    public async Task CountSourceRows_SchemaOutsideTheOperationalSet_IsNotFoundBeforeConnecting(string schema)
    {
        var count = () => Service().CountSourceRowsAsync(UnreachableConnection, schema, "big");

        var refusal = (await count.Should().ThrowAsync<LayerPublishingException>()).Which;
        refusal.ErrorKind.Should().Be(LayerPublishingErrorKind.NotFound);
        refusal.Message.Should().Be($"Table '{schema.Trim()}.big' was not found.");
    }

    [Fact]
    public async Task CountSourceRows_CommittedSinkScope_AdmitsAnUnlistedSchemaToTheDatabase()
    {
        using var committed = CommittedExternalSchemaPublication.Begin();

        var count = () => Service().CountSourceRowsAsync(UnreachableConnection, "v9393", "big");

        (await count.Should().ThrowAsync<Exception>()).Which.Should().NotBeOfType<LayerPublishingException>();
    }

    private static PostgreSqlLayerPublishingService Service()
        => new(new Mock<ITableDiscoveryService>(MockBehavior.Strict).Object,
            new Mock<IMetadataV2GraphStore>(MockBehavior.Strict).Object,
            NullLogger<PostgreSqlLayerPublishingService>.Instance);
}
