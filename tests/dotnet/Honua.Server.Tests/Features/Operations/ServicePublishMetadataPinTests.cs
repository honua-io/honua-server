// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsPublishing;

// An agent's publication proposal is pinned to the metadata write base it was proposed against;
// the approved replay publishes only onto that base. Direct publishes keep their retry semantics.
public sealed class ServicePublishMetadataPinTests
{
    private readonly ILayerPublishingService _publishing =
        Substitute.For<ILayerPublishingService, ILayerPublicationBaseReader>();
    private readonly ServicePublishExecutor _executor;

    public ServicePublishMetadataPinTests()
    {
        ((ILayerPublicationBaseReader)_publishing).ReadPublicationBaseAsync(Arg.Any<CancellationToken>())
            .Returns(new LayerPublicationBase(7, "\"etag-7\""));
        _publishing.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PublishedLayerSummary
            {
                StorageLayerId = 321,
                LayerName = "Parcels",
                Schema = "public",
                Table = "parcels",
                GeometryType = "Point",
                Srid = 4326,
                ServiceName = "parcels",
                MetadataRevision = 8,
            });
        _executor = new ServicePublishExecutor(
            _publishing, Substitute.For<ISecureConnectionResolver>(), Substitute.For<IMetadataV2GraphProvider>(),
            TimeProvider.System);
    }

    [UnitTest]
    public async Task PrepareAsync_Proposal_PinsTheServerReadWriteBase()
    {
        var prepared = await _executor.PrepareAsync(Request(), Context(), CancellationToken.None);

        prepared.Parameters[ServicePublishMetadataPin.RevisionKey].Should().Be("7");
        prepared.Parameters[ServicePublishMetadataPin.EtagKey].Should().Be("\"etag-7\"");
    }

    [UnitTest]
    public async Task PrepareAsync_CallerSuppliedPin_IsRefused()
    {
        var forged = Request((ServicePublishMetadataPin.RevisionKey, "1"), (ServicePublishMetadataPin.EtagKey, "\"old\""));

        var act = () => _executor.PrepareAsync(forged, Context(), CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*server-set*");
    }

    [UnitTest]
    public async Task PrepareAsync_ApprovedReplayWithoutPin_IsRefused()
    {
        var act = () => _executor.PrepareAsync(Request(), Context() with { ApprovedProposalId = "proposal-1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*approved metadata base*");
    }

    [UnitTest]
    public async Task SubmitAsync_ApprovedReplay_PublishesOnlyOntoThePinnedBase()
    {
        var replay = Request((ServicePublishMetadataPin.RevisionKey, "7"), (ServicePublishMetadataPin.EtagKey, "\"etag-7\""));

        await _executor.SubmitAsync(replay, Context() with { ApprovedProposalId = "proposal-1" }, CancellationToken.None);

        await _publishing.Received(1).PublishLayerAsync(Arg.Any<string>(),
            Arg.Is<LayerPublishRequest>(request => request.ExpectedMetadataRevision == 7
                && request.ExpectedMetadataEtag == "\"etag-7\""),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SubmitAsync_DirectPublish_KeepsUnpinnedRetrySemantics()
    {
        var prepared = await _executor.PrepareAsync(Request(), Context(), CancellationToken.None);

        await _executor.SubmitAsync(prepared, Context(), CancellationToken.None);

        await _publishing.Received(1).PublishLayerAsync(Arg.Any<string>(),
            Arg.Is<LayerPublishRequest>(request => request.ExpectedMetadataRevision == null
                && request.ExpectedMetadataEtag == null),
            Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task SubmitAsync_ApprovedReplayOnAMovedCatalog_FailsAsAFreshProposalRequired()
    {
        PublishThrows(LayerPublicationBaseChanged.Create());

        var handle = await _executor.SubmitAsync(PinnedReplay(),
            Context() with { ApprovedProposalId = "proposal-1", OperationInstanceId = "opinst-1" },
            CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.Reason.Should().Be(LayerPublicationBaseChanged.ProposalReason);
        handle.OperationInstanceId.Should().Be("opinst-1");
    }

    [UnitTest]
    public async Task SubmitAsync_ApprovedInlineJobOnAMovedCatalog_FailsAsAFreshProposalRequired()
    {
        var jobs = Substitute.For<ILayerPublishJobs>();
        jobs.SubmitAsync(Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<LayerPublishRequest>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<LayerPublishAdmission>(LayerPublicationBaseChanged.Create()));
        var executor = new ServicePublishExecutor(
            _publishing, Substitute.For<ISecureConnectionResolver>(), Substitute.For<IMetadataV2GraphProvider>(),
            TimeProvider.System, jobs: jobs);

        var handle = await executor.SubmitAsync(PinnedReplay(), Context() with { ApprovedProposalId = "proposal-1" },
            CancellationToken.None);

        handle.Status.Should().Be(OperationHandleStatus.Failed);
        handle.Reason.Should().Be(LayerPublicationBaseChanged.ProposalReason);
    }

    [UnitTest]
    public async Task SubmitAsync_DirectPublishOnAMovedCatalog_KeepsTheConflict()
    {
        PublishThrows(LayerPublicationBaseChanged.Create());

        var act = () => _executor.SubmitAsync(Request(), Context(), CancellationToken.None);

        (await act.Should().ThrowAsync<LayerPublishingException>()).Which.Message
            .Should().Be(LayerPublicationBaseChanged.Message);
    }

    [UnitTest]
    public async Task SubmitAsync_ApprovedReplayWithAnotherConflict_IsNotReportedAsStale()
    {
        PublishThrows(new LayerPublishingException(LayerPublishingErrorKind.Conflict, "Layer name already exists."));

        var act = () => _executor.SubmitAsync(PinnedReplay(), Context() with { ApprovedProposalId = "proposal-1" },
            CancellationToken.None);

        await act.Should().ThrowAsync<LayerPublishingException>().WithMessage("Layer name already exists.");
    }

    [UnitTest]
    public void QueuedPayload_CarriesThePin()
    {
        var payload = new LayerPublishJobPayload
        {
            ConnectionKey = "c",
            RowCount = 1,
            Request = new Honua.Server.Features.Admin.Models.PublishLayerRequest
            {
                Schema = "public",
                Table = "parcels",
                LayerName = "Parcels",
            },
            ExpectedMetadataRevision = 7,
            ExpectedMetadataEtag = "\"etag-7\"",
        };

        LayerPublishJobPayload.TryParse(
            new Dictionary<string, string> { [LayerPublishJobPayload.ParameterName] = payload.ToJson() },
            out var parsed, out _).Should().BeTrue();

        parsed!.ExpectedMetadataRevision.Should().Be(7);
        parsed.ExpectedMetadataEtag.Should().Be("\"etag-7\"");
    }

    private static OperationRequest Request(params (string Key, string Value)[] extra)
    {
        var parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["schema"] = "public",
            ["table"] = "parcels",
            ["layerName"] = "Parcels",
        };
        foreach (var (key, value) in extra)
        {
            parameters[key] = value;
        }

        return new OperationRequest
        {
            OperationId = "service.publish",
            ConnectionId = "11111111-1111-1111-1111-111111111111",
            ServiceName = "parcels",
            Parameters = parameters,
        };
    }

    private static OperationRequest PinnedReplay()
        => Request((ServicePublishMetadataPin.RevisionKey, "7"), (ServicePublishMetadataPin.EtagKey, "\"etag-7\""));

    private void PublishThrows(LayerPublishingException exception)
        => _publishing.PublishLayerAsync(Arg.Any<string>(), Arg.Any<LayerPublishRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<PublishedLayerSummary>(exception));

    private static OperationPolicyContext Context() => new()
    {
        ResolvedConnectionString = "Host=localhost;Database=test",
        PrincipalId = "agent-1",
    };
}
