// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Features.Operations;

internal enum ServicePublishPinState
{
    NotChecked = 0,

    Current = 1,

    Stale = 2,
}

// Says whether a service.publish proposal can still succeed against the catalog it pinned.
internal static class ServicePublishProposalFreshness
{
    // A replay that failed on its stale pin needs a fresh proposal, the same as one found stale before approval.
    public static bool FailedOnStalePin(OperationProposal proposal, string? status, string? resolutionReason)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        return IsServicePublish(proposal)
            && string.Equals(status, nameof(OperationProposalStatus.Failed), StringComparison.Ordinal)
            && string.Equals(resolutionReason, LayerPublicationBaseChanged.ProposalReason, StringComparison.Ordinal);
    }

    // Before approval: compares the pinned write base with the current one, the same check the approved replay makes.
    public static async Task<ServicePublishPinState> ReadPinStateAsync(
        OperationProposal proposal, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(services);
        if (!IsServicePublish(proposal)
            || proposal.Status is not OperationProposalStatus.AwaitingApproval
            || ReadPin(proposal.Plan.ExecutionPayload) is not { } pin
            || services.GetService<ILayerPublishingService>() is not ILayerPublicationBaseReader baseReader)
        {
            return ServicePublishPinState.NotChecked;
        }

        var current = await baseReader.ReadPublicationBaseAsync(cancellationToken).ConfigureAwait(false);
        return current.Revision == pin.Revision && string.Equals(current.Etag, pin.Etag, StringComparison.Ordinal)
            ? ServicePublishPinState.Current
            : ServicePublishPinState.Stale;
    }

    private static bool IsServicePublish(OperationProposal proposal)
        => string.Equals(proposal.OperationId, ServicePublishOperation.OperationId, StringComparison.Ordinal);

    private static LayerPublicationBase? ReadPin(string? executionPayload)
    {
        if (string.IsNullOrWhiteSpace(executionPayload))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize(
                executionPayload, ServicePublishApprovalJsonContext.Default.ServicePublishApprovalPayload);
            return payload is null ? null : ServicePublishMetadataPin.TryRead(payload.ToOperationRequest());
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
