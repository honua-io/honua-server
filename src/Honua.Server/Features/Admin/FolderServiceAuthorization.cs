// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Authentication;
using OperatorAccessDecision = Honua.Core.Features.Security.Abstractions.AccessDecision;

namespace Honua.Server.Features.Admin;

internal static class FolderServiceAuthorization
{
    internal static async Task<FolderAuthorizationResult> EvaluateAsync(
        FolderAuthorizationContext context,
        CancellationToken cancellationToken = default)
    {
        var evaluations = new List<ServiceAccessEvaluation>();
        var requester = context.RequesterAccess with
        {
            Operation = AuthorizationOperation.Admin,
            RequestCache = ReferenceEquals(context.OperatorPrincipal, context.RequesterAccess.Principal)
                ? context.RequesterAccess.RequestCache : null
        };
        var affected = context.Mutation.AffectedServices(context.Original)
            .GroupBy(service => service.Metadata.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var services in affected)
        {
            if (services.Any(service => !MetadataV2ServicePaths.SameTenant(service.Metadata.Tenant, context.FolderTenant)))
                return new(evaluations, new FolderAuthorizationFailure.Tenant());
            var gate = context.Services.GetRequiredService<OperatorApprovalGate>();
            var decision = await gate.CheckAuthorizationAsync(context.OperatorPrincipal, new OperatorAuthorizationRequest
            {
                ResourceType = OperatorResourceType.PublishedService,
                Operation = OperatorOperation.Update,
                ResourceId = services.Key
            }, cancellationToken).ConfigureAwait(false);
            if (!decision.IsAllowed) return new(evaluations, new FolderAuthorizationFailure.Operator(decision));
            foreach (var service in services)
            {
                var evaluation = await AccessPolicyHelpers.EvaluateServiceAccessCoreAsync(requester,
                    service, cancellationToken).ConfigureAwait(false);
                evaluations.Add(evaluation);
                if (!evaluation.Decision.IsAllowed)
                    return new(evaluations, new FolderAuthorizationFailure.Requester(evaluation));
            }
        }
        return new(evaluations, null);
    }
}

internal sealed record FolderAuthorizationContext
{
    public required MetadataV2Graph Original { get; init; }
    public required FolderMutation Mutation { get; init; }
    public required IServiceProvider Services { get; init; }
    public required ClaimsPrincipal OperatorPrincipal { get; init; }
    public required ServiceAccessContext RequesterAccess { get; init; }
    public string? FolderTenant { get; init; }
}

internal sealed record FolderAuthorizationResult(
    IReadOnlyList<ServiceAccessEvaluation> RequesterEvaluations, FolderAuthorizationFailure? Failure);

internal abstract record FolderAuthorizationFailure
{
    internal sealed record Tenant : FolderAuthorizationFailure;
    internal sealed record Operator(OperatorAccessDecision Decision) : FolderAuthorizationFailure;
    internal sealed record Requester(ServiceAccessEvaluation Evaluation) : FolderAuthorizationFailure;
}
