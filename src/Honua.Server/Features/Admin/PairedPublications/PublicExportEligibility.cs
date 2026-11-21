// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed record PublicExportEligibilityResult
{
    public bool IsEligible { get; init; }

    public string? Reason { get; init; }

    public static PublicExportEligibilityResult Eligible { get; } = new() { IsEligible = true };

    public static PublicExportEligibilityResult Ineligible(string reason) => new() { IsEligible = false, Reason = reason };
}

// A public Export is served directly from object storage / CDN with no server in the request path.
// It may be promoted only when an anonymous reader would see every row and every field.
internal sealed class PublicExportEligibility(
    IServiceProvider services,
    IOptions<TenantIsolationOptions>? tenantIsolation = null,
    IOptions<TenantContextOptions>? tenantContext = null)
{
    private static readonly ClaimsPrincipal AnonymousPrincipal = new(new ClaimsIdentity());

    internal const string ReasonMultiTenancyEnabled = "stays on signed-in routes: multi-tenancy is enabled";
    internal const string ReasonTenantScoped = "stays on signed-in routes: layer is tenant-scoped";
    internal const string ReasonAnonymousDenied = "stays on signed-in routes: anonymous read is not allowed";
    internal const string ReasonRowFilter = "stays on signed-in routes: row filter";
    internal const string ReasonHiddenField = "stays on signed-in routes: hidden field";
    internal const string ReasonPolicyEvaluationFailed = "stays on signed-in routes: read policy could not be verified";
    internal const string ReasonNotPubliclyPublished = "stays on signed-in routes: layer is no longer publicly published";

    public Task<PublicExportEligibilityResult> EvaluateSnapshotAsync(
        MetadataV2GraphSnapshot snapshot,
        string serviceId,
        string resourceId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (!snapshot.Index.ServicesById.TryGetValue(serviceId, out var service) ||
            !snapshot.Index.ResourcesById.TryGetValue(resourceId, out var resource))
        {
            return Task.FromResult(PublicExportEligibilityResult.Ineligible(ReasonNotPubliclyPublished));
        }

        var publication = snapshot.Graph.Publications.FirstOrDefault(candidate =>
            snapshot.IsRoutable(candidate) &&
            string.Equals(candidate.ServiceId, serviceId, StringComparison.Ordinal) &&
            string.Equals(candidate.ResourceId, resourceId, StringComparison.Ordinal));
        if (publication is null ||
            !MetadataV2TenantVisibility.IsVisibleToTenant(publication, resource, service, requestTenant: null))
        {
            return Task.FromResult(PublicExportEligibilityResult.Ineligible(ReasonNotPubliclyPublished));
        }

        return EvaluateAsync(resource, service, cancellationToken);
    }

    public async Task<PublicExportEligibilityResult> EvaluateAsync(
        MetadataV2Object resource,
        MetadataV2Service service,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(service);

        if (EvaluateStaticRestrictions(resource, service) is { } staticFailure)
        {
            return staticFailure;
        }

        var accessDecision = await AccessPolicyHelpers.EvaluateResourceAccessCoreAsync(
            services,
            AnonymousPrincipal,
            tenantId: null,
            applyTenantScope: false,
            resource,
            service,
            AuthorizationOperation.Export,
            cancellationToken).ConfigureAwait(false);
        if (!accessDecision.IsAllowed)
        {
            return PublicExportEligibilityResult.Ineligible(ReasonAnonymousDenied);
        }

        return await EvaluateAnonymousPoliciesAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    private PublicExportEligibilityResult? EvaluateStaticRestrictions(
        MetadataV2Object resource,
        MetadataV2Service service)
    {
        if (tenantIsolation?.Value.Enabled == true || tenantContext?.Value.Enabled == true)
        {
            return PublicExportEligibilityResult.Ineligible(ReasonMultiTenancyEnabled);
        }

        if (!MetadataV2TenantVisibility.IsVisibleToTenant(resource, requestTenant: null) ||
            !MetadataV2TenantVisibility.IsVisibleToTenant(service, requestTenant: null))
        {
            return PublicExportEligibilityResult.Ineligible(ReasonTenantScoped);
        }

        if (!string.IsNullOrWhiteSpace(resource.PermanentFilter?.Expression))
        {
            return PublicExportEligibilityResult.Ineligible(ReasonRowFilter);
        }

        return resource.SchemaFields.Any(field => field.Hidden)
            ? PublicExportEligibilityResult.Ineligible(ReasonHiddenField)
            : null;
    }

    private async Task<PublicExportEligibilityResult> EvaluateAnonymousPoliciesAsync(
        MetadataV2Object resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var resolver = CreateAnonymousSecurityResolver();
            var query = await resolver.ApplyAsync(resource, new FeatureQuery(), cancellationToken).ConfigureAwait(false);
            if (query.EnforcedSqlFilter is not null)
            {
                return PublicExportEligibilityResult.Ineligible(ReasonRowFilter);
            }

            return query.EnforcedMaskedFields is { IsDefaultOrEmpty: false }
                ? PublicExportEligibilityResult.Ineligible(ReasonHiddenField)
                : PublicExportEligibilityResult.Eligible;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return PublicExportEligibilityResult.Ineligible(ReasonPolicyEvaluationFailed);
        }
    }

    private LayerReadSecurityResolver CreateAnonymousSecurityResolver()
    {
        var filterExpressionService = services.GetService<IFilterExpressionService>();
        var graphProvider = services.GetService<IMetadataV2GraphProvider>();
        var rbacOptions = services.GetService<IOptions<RbacOptions>>() ?? Options.Create(new RbacOptions());
        var anonymousAccessor = new StaticHttpContextAccessor(new DefaultHttpContext
        {
            User = AnonymousPrincipal,
            RequestServices = services
        });

        IRowLevelSecurityFilterSource? rlsSource = services.GetService<IRlsPolicyStore>() is { } rlsStore &&
            graphProvider is not null && filterExpressionService is not null
            ? new RowLevelSecurityFilterSource(
                anonymousAccessor, rlsStore, graphProvider, filterExpressionService, rbacOptions,
                services.GetRequiredService<ILogger<RowLevelSecurityFilterSource>>(), services)
            : services.GetService<IRowLevelSecurityFilterSource>();

        IFieldMaskSource? fieldMaskSource = services.GetService<IFieldMaskPolicyStore>() is { } maskStore &&
            graphProvider is not null
            ? new FieldMaskSource(
                anonymousAccessor, maskStore, graphProvider, rbacOptions,
                services.GetRequiredService<ILogger<FieldMaskSource>>(), services)
            : services.GetService<IFieldMaskSource>();

        return new LayerReadSecurityResolver(graphProvider, filterExpressionService, rlsSource, fieldMaskSource);
    }

    private sealed class StaticHttpContextAccessor(HttpContext context) : IHttpContextAccessor
    {
        public HttpContext? HttpContext
        {
            get => context;
            set => _ = value;
        }
    }
}
