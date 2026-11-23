// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Features.Admin;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Admin;

// The admission count applies the publish's schema gate: a plain request counts with no
// committed-sink scope, a committed sink request counts inside one and leaves it closed.
public sealed partial class LayerPublishJobTests
{
    [UnitTest]
    public async Task Submit_PlainRequest_CountsWithoutCommittedSchemaScope()
    {
        var scopeActive = new List<bool>();
        var publisher = ScopeRecordingPublisher(scopeActive);

        await Service(publisher, LayerPublishRunnerMode.Unavailable, threshold: 10)
            .SubmitAsync(Connection, Guid.NewGuid(), DomainRequest(), "roads", CancellationToken.None);

        Assert.Equal([false], scopeActive);
    }

    [UnitTest]
    public async Task Submit_CommittedSinkRequest_CountsInsideCommittedSchemaScopeOnly()
    {
        var scopeActive = new List<bool>();
        var publisher = ScopeRecordingPublisher(scopeActive);
        var connectionId = Guid.NewGuid();
        var fingerprint = ConnectionEndpointFingerprint.TryCompute(Connection)!;
        var request = new LayerPublishRequest
        {
            Schema = "sink_out",
            Table = "roads",
            LayerName = "Roads",
            ConnectionEndpointFingerprint = fingerprint,
            CommittedSinkPublication = new VerifiedCommittedSinkPublication
            {
                SourceJobId = "sink-job",
                SourceArtifactId = "sink-job:artifact:1",
                ConnectionId = connectionId,
                Schema = "sink_out",
                Table = "roads",
                EndpointFingerprint = fingerprint,
                SourceOwnerActorId = "alice",
                PublisherActorId = "alice",
                TenantId = "tenant-a"
            }
        };

        var refusal = await Assert.ThrowsAsync<LayerPublishingException>(() =>
            Service(publisher, LayerPublishRunnerMode.DurableLocal, threshold: 10)
                .SubmitAsync(Connection, connectionId, request, "roads", CancellationToken.None));

        Assert.Equal(LayerPublishingErrorKind.Validation, refusal.ErrorKind);
        Assert.Equal([true], scopeActive);
        Assert.False(CommittedExternalSchemaPublication.IsActive);
    }

    private static ILayerPublishingService ScopeRecordingPublisher(List<bool> scopeActive)
    {
        var publisher = Substitute.For<ILayerPublishingService>();
        publisher.CountSourceRowsAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                scopeActive.Add(CommittedExternalSchemaPublication.IsActive);
                return 11L;
            });
        return publisher;
    }
}
