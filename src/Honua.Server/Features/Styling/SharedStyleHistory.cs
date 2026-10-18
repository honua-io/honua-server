// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Geoprocessing;
using Honua.Infrastructure.Authentication;
using Honua.Server.Features.Operations;

namespace Honua.Server.Features.Styling;

/// <summary>Restores retained canonical before-bytes through a NEW reviewed operation, never by decrementing a version.</summary>
internal static class SharedStyleHistory
{
    internal static async Task<SharedStyleEditIntent> LoadRestoreAsync(HttpContext context, SharedStyleTurnBinding current,
        string sourceProposalId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sourceProposalId) || sourceProposalId.Length > 200 || sourceProposalId.Any(char.IsControl)) Missing();
        var proposal = await context.RequestServices.GetRequiredService<IOperationProposalStore>().GetAsync(sourceProposalId, cancellationToken).ConfigureAwait(false);
        if (proposal is null || proposal.OperationId != SharedStyleEditOperation.OperationId || proposal.Status != OperationProposalStatus.Succeeded ||
            proposal.Plan.ExecutionPayload is null || proposal.SealedPlanHash is null ||
            proposal.SealedPlanHash != OperationApprovalPlanSeal.Compute(proposal.Plan)) Missing();
        var payload = JsonSerializer.Deserialize(proposal!.Plan.ExecutionPayload!, SharedStyleGovernanceJsonContext.Default.SharedStyleApprovalPayload);
        if (payload is null || payload.ActorId != proposal.RequestedBy || payload.OwnerActorId != current.Binding.OwnerActorId ||
            payload.TenantId != current.Binding.TenantId || payload.TargetId != current.Binding.TargetId ||
            payload.SchemaName != context.RequestServices.GetService<Honua.Core.Features.Infrastructure.Abstractions.ISchemaContext>()?.CurrentSchema ||
            payload.Impact.StyleId != current.Task.Id ||
            string.IsNullOrWhiteSpace(payload.BeforeMapLibreJson)) Missing();
        var graph = await context.RequestServices.GetRequiredService<IMetadataV2GraphProvider>().GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        // Historical scope cannot become a backdoor to references no longer visible now.
        foreach (var association in payload!.Impact.Associations)
        {
            if (!graph.Index.ResourcesById.TryGetValue(association.ResourceId, out var resource)) Missing();
            var service = association.ServiceId is null ? null : graph.Index.ServicesById.GetValueOrDefault(association.ServiceId);
            if (association.ServiceId is not null && service is null || !TenantScopeHelpers.IsTenantVisible(context, resource!, service) ||
                !(await AccessPolicyHelpers.EvaluateResourceAccessAsync(context, resource!, service,
                    AuthorizationOperation.Metadata, cancellationToken).ConfigureAwait(false)).IsAllowed) Missing();
        }
        var studio = context.RequestServices.GetRequiredService<IStudioPackageStore>();
        var authorization = context.RequestServices.GetRequiredService<IStudioAuthorizationService>();
        foreach (var consumer in payload.StoreSnapshot.Consumers)
        {
            string? owner;
            string? tenant;
            if (consumer.VersionId is { } versionId)
            {
                var version = await studio.GetVersionAsync(consumer.ItemId, versionId, cancellationToken).ConfigureAwait(false);
                if (version is null || version.ContentHash != consumer.ContentHash) Missing();
                owner = version!.OwnerId;
                tenant = version.TenantId;
            }
            else if (consumer.DraftId is { } draftId)
            {
                var draft = await studio.GetDraftAsync(draftId, cancellationToken).ConfigureAwait(false);
                if (draft is null || draft.ItemId != consumer.ItemId) Missing();
                owner = draft!.OwnerId;
                tenant = draft.TenantId;
            }
            else throw new GeoprocessingNotFoundException("Retained consumer identity cannot be verified.");
            var allowed = await authorization.AuthorizeAsync(context.User, authorization.ResolveCallerId(context.User),
                consumer.DraftId.HasValue ? StudioAuthorizationOperation.ReadDraft : StudioAuthorizationOperation.ReadContentItem,
                owner, tenant, false, (consumer.DraftId ?? consumer.ItemId).ToString("D"), cancellationToken).ConfigureAwait(false);
            if (!allowed.IsAllowed) Missing();
        }
        using var before = JsonDocument.Parse(payload.BeforeMapLibreJson);
        if (StyleEditHashes.Content(payload.BeforeMapLibreJson) != payload.Impact.ContentHash) Missing();
        return new(current.Task.Id, checked((int)current.Task.Revision), current.Task.ContentHash!, current.Task.ImpactHash!, before.RootElement.Clone());
    }

    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    private static void Missing() => throw new GeoprocessingNotFoundException("A retained, verifiable, authorized successful style receipt is required; a version number alone cannot restore appearance.");
}
