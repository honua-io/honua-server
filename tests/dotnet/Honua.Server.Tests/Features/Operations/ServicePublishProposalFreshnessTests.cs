// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using NSubstitute;

namespace Honua.Server.Tests.Features.OperationsPublishing;

// A publish proposal pinned to a catalog revision that has since moved cannot succeed;
// the approver and the agent are told to submit a fresh proposal before and after approval.
public sealed class ServicePublishProposalFreshnessTests
{
    private readonly ILayerPublishingService _publishing =
        Substitute.For<ILayerPublishingService, ILayerPublicationBaseReader>();
    private readonly IServiceProvider _services = Substitute.For<IServiceProvider>();

    public ServicePublishProposalFreshnessTests()
    {
        ((ILayerPublicationBaseReader)_publishing).ReadPublicationBaseAsync(Arg.Any<CancellationToken>())
            .Returns(new LayerPublicationBase(7, "\"etag-7\""));
        _services.GetService(typeof(ILayerPublishingService)).Returns(_publishing);
    }

    [UnitTest]
    public async Task ReadPinStateAsync_PinMatchesCatalog_IsCurrent()
    {
        var state = await ServicePublishProposalFreshness.ReadPinStateAsync(
            Proposal("7", "\"etag-7\""), _services, CancellationToken.None);

        state.Should().Be(ServicePublishPinState.Current);
    }

    [UnitTest]
    public async Task ReadPinStateAsync_AnotherPublishMovedTheCatalog_IsStale()
    {
        var state = await ServicePublishProposalFreshness.ReadPinStateAsync(
            Proposal("6", "\"etag-6\""), _services, CancellationToken.None);

        state.Should().Be(ServicePublishPinState.Stale);
    }

    [UnitTest]
    public async Task ReadPinStateAsync_SameRevisionDifferentEtag_IsStale()
    {
        var state = await ServicePublishProposalFreshness.ReadPinStateAsync(
            Proposal("7", "\"etag-other\""), _services, CancellationToken.None);

        state.Should().Be(ServicePublishPinState.Stale);
    }

    [UnitTest]
    public async Task ReadPinStateAsync_ResolvedProposal_IsNotChecked()
    {
        var resolved = Proposal("6", "\"etag-6\"") with { Status = OperationProposalStatus.Succeeded };

        var state = await ServicePublishProposalFreshness.ReadPinStateAsync(resolved, _services, CancellationToken.None);

        state.Should().Be(ServicePublishPinState.NotChecked);
        await ((ILayerPublicationBaseReader)_publishing).DidNotReceive()
            .ReadPublicationBaseAsync(Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task ReadPinStateAsync_OtherOperation_IsNotChecked()
    {
        var other = Proposal("6", "\"etag-6\"") with { OperationId = "admin.config.change" };

        var state = await ServicePublishProposalFreshness.ReadPinStateAsync(other, _services, CancellationToken.None);

        state.Should().Be(ServicePublishPinState.NotChecked);
    }

    [UnitTest]
    public void FailedOnStalePin_RecordedFreshnessReason_IsTrue()
    {
        var failed = Proposal("6", "\"etag-6\"");

        ServicePublishProposalFreshness.FailedOnStalePin(failed, "Failed", LayerPublicationBaseChanged.ProposalReason)
            .Should().BeTrue();
        ServicePublishProposalFreshness.FailedOnStalePin(failed, "Failed", "Operation actuation failed.")
            .Should().BeFalse();
        ServicePublishProposalFreshness.FailedOnStalePin(failed, "Succeeded", LayerPublicationBaseChanged.ProposalReason)
            .Should().BeFalse();
    }

    private static OperationProposal Proposal(string revision, string etag)
    {
        var payload = new ServicePublishApprovalPayload
        {
            ConnectionId = "conn-1",
            Schema = "public",
            Table = "parcels",
            LayerName = "Parcels",
            Parameters = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["schema"] = "public",
                ["table"] = "parcels",
                ["layerName"] = "Parcels",
                [ServicePublishMetadataPin.RevisionKey] = revision,
                [ServicePublishMetadataPin.EtagKey] = etag,
            },
        };
        var now = DateTimeOffset.UtcNow;
        return new OperationProposal
        {
            ProposalId = $"proposal-{Guid.NewGuid():N}",
            TenantId = "public",
            Kind = OperationClass.ServicePublish,
            Status = OperationProposalStatus.AwaitingApproval,
            OperationId = ServicePublishOperation.OperationId,
            Plan = new OperationProposalPlan
            {
                Summary = "Publish 'public.parcels' as layer 'Parcels'.",
                RiskLevel = ProposalRiskLevel.Medium,
                ExecutionPayload = JsonSerializer.Serialize(
                    payload, ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload),
            },
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
