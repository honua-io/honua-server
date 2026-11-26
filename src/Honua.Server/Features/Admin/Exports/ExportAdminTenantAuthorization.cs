// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

namespace Honua.Server.Features.Admin.Exports;

internal sealed record ExportVisibilitySubject
{
    public required string ExportId { get; init; }

    public string? ServiceId { get; init; }

    public string? ResourceId { get; init; }

    public int? LayerId { get; init; }

    public string? JobId { get; init; }
}

internal static class ExportAdminTenantAuthorization
{
    private readonly record struct ExportTenantScope(HttpContext HttpContext, MetadataV2GraphSnapshot Snapshot);

    public static async Task<Func<ExportVisibilitySubject, CancellationToken, ValueTask<bool>>?> CreateVisibilityFilterAsync(
        HttpContext context,
        CancellationToken cancellationToken)
    {
        if (OperationTenantAuthorization.HasCrossTenantAccess(context))
        {
            return null;
        }

        var snapshot = await context.RequestServices
            .GetRequiredService<IMetadataV2GraphProvider>()
            .GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        var scope = new ExportTenantScope(context, snapshot);
        return (subject, ct) => IsExportVisibleAsync(scope, subject, ct);
    }

    public static async Task<IResult?> AuthorizeCreateAsync(
        HttpContext context,
        PMTilesPublishedSource source,
        CancellationToken cancellationToken)
    {
        var hasCrossTenantAccess = OperationTenantAuthorization.HasCrossTenantAccess(context);
        if (!hasCrossTenantAccess &&
            !TenantScopeHelpers.IsPublicationVisible(context, source.Publication, source.Resource, source.Service))
        {
            return ExportAdminEndpoints.CreateNotFoundFailure("The source is not a routable Layer of that Service.");
        }

        if (context.RequestServices.GetService<IAccessPolicyEvaluator>() is null)
        {
            return null;
        }

        var decision = await AccessPolicyHelpers.EvaluateResourceAccessCoreAsync(
            context.RequestServices,
            context.User,
            TenantScopeHelpers.ResolveRequestTenantId(context),
            applyTenantScope: !hasCrossTenantAccess,
            source.Resource,
            source.Service,
            AuthorizationOperation.Export,
            cancellationToken,
            requestCache: context.Items).ConfigureAwait(false);
        return AccessPolicyHelpers.CreateAccessDeniedResult(context, decision);
    }

    private static async ValueTask<bool> IsExportVisibleAsync(
        ExportTenantScope scope,
        ExportVisibilitySubject subject,
        CancellationToken cancellationToken)
    {
        var (jobChecked, jobAllowed) = await EvaluateJobAccessAsync(scope.HttpContext, subject.JobId, cancellationToken)
            .ConfigureAwait(false);
        if (jobChecked && !jobAllowed)
        {
            return false;
        }

        var sourceVisible = TryEvaluateSourceVisibility(scope, subject);
        if (sourceVisible.HasValue)
        {
            return sourceVisible.Value;
        }

        return jobChecked;
    }

    private static async ValueTask<(bool Checked, bool Allowed)> EvaluateJobAccessAsync(
        HttpContext context,
        string? jobId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return (false, false);
        }

        var jobStore = context.RequestServices.GetService<IExecutionJobStore>();
        var job = jobStore is null
            ? null
            : await jobStore.GetAsync(jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return (false, false);
        }

        var allowed = OperationTenantAuthorization.CanAccess(context, OperationTenantAuthorization.OwnerTenantOf(job));
        return (true, allowed);
    }

    private static bool? TryEvaluateSourceVisibility(
        ExportTenantScope scope,
        ExportVisibilitySubject subject)
    {
        var service = ResolveService(scope.Snapshot, subject.ServiceId);
        var resource = ResolveResource(scope.Snapshot, subject.ResourceId);
        if (service is not null || resource is not null)
        {
            return IsResolvedSourceVisible(scope, service, resource);
        }

        if (subject.LayerId is not { } layerId)
        {
            return null;
        }

        var published = PMTilesProxySourceResolver.ResolveForPublish(scope.Snapshot, subject.ServiceId, layerId);
        return published is null
            ? null
            : TenantScopeHelpers.IsPublicationVisible(scope.HttpContext, published.Publication, published.Resource, published.Service);
    }

    private static bool IsResolvedSourceVisible(
        ExportTenantScope scope,
        MetadataV2Service? service,
        MetadataV2Object? resource)
    {
        if (!TenantScopeHelpers.IsTenantVisible(scope.HttpContext, resource, service))
        {
            return false;
        }

        if (service is null || resource is null)
        {
            return true;
        }

        var matching = scope.Snapshot.PublicationsForService(service.Metadata.Id)
            .Where(publication => string.Equals(publication.ResourceId, resource.Metadata.Id, StringComparison.Ordinal))
            .ToArray();
        return matching.Length == 0 ||
            matching.Any(publication => TenantScopeHelpers.IsPublicationVisible(scope.HttpContext, publication, resource, service));
    }

    private static MetadataV2Service? ResolveService(MetadataV2GraphSnapshot snapshot, string? serviceId)
    {
        if (string.IsNullOrWhiteSpace(serviceId))
        {
            return null;
        }

        return snapshot.Index.ServicesById.TryGetValue(serviceId, out var byId)
            ? byId
            : snapshot.FindService(serviceId);
    }

    private static MetadataV2Object? ResolveResource(MetadataV2GraphSnapshot snapshot, string? resourceId)
        => !string.IsNullOrWhiteSpace(resourceId) &&
            snapshot.Index.ResourcesById.TryGetValue(resourceId, out var resource)
            ? resource
            : null;
}
