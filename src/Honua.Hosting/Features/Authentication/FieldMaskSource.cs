// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

/// <summary>
/// Request-scoped <see cref="IFieldMaskSource"/> (#1940). Resolves the current principal's
/// roles and the layer's <see cref="FieldMaskPolicy"/> set into the union of attribute
/// (field) names that must be masked (dropped) from query output. This is the field-level
/// companion to <see cref="RowLevelSecurityFilterSource"/>; masking is enforced inside the
/// feature store at the shared projection seam, so a masked attribute is removed even when
/// the caller explicitly requests it.
/// </summary>
/// <remarks>
/// Returning an empty set means "no masking applies". When there is no request context
/// (e.g. a background job), no masking is applied here — field masking is a request-scoped
/// concern keyed on the caller's roles.
/// </remarks>
internal sealed partial class FieldMaskSource : IFieldMaskSource
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IFieldMaskPolicyStore _policyStore;
    private readonly IMetadataV2GraphProvider _graphProvider;
    private readonly RbacOptions _rbacOptions;
    private readonly ILogger<FieldMaskSource> _logger;

    public FieldMaskSource(
        IHttpContextAccessor httpContextAccessor,
        IFieldMaskPolicyStore policyStore,
        IMetadataV2GraphProvider graphProvider,
        IOptions<RbacOptions> rbacOptions,
        ILogger<FieldMaskSource> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _policyStore = policyStore;
        _graphProvider = graphProvider;
        _rbacOptions = rbacOptions.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ImmutableArray<string>> ResolveAsync(MetadataV2Resource resource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);

        var principal = _httpContextAccessor.HttpContext?.User;
        if (principal is null)
        {
            // No request context (e.g. background job) — masking is request-scoped.
            return ImmutableArray<string>.Empty;
        }

        var roles = RbacRoleClaims.Enumerate(
            principal,
            _rbacOptions,
            _httpContextAccessor.HttpContext?.RequestServices);

        var layerName = resource.Metadata.Name;
        if (string.IsNullOrWhiteSpace(layerName))
        {
            return ImmutableArray<string>.Empty;
        }

        // A resource can be published through several services; collect the candidate
        // service names so a service-scoped policy matches regardless of which protocol
        // surface served the request. "*" service policies match all.
        var serviceNames = await ResolveServiceNamesAsync(resource, cancellationToken).ConfigureAwait(false);

        var maskedFields = await CollectMaskedFieldsAsync(roles, serviceNames, layerName, cancellationToken).ConfigureAwait(false);
        return maskedFields.Count == 0 ? ImmutableArray<string>.Empty : maskedFields.ToImmutableArray();
    }

    private async Task<HashSet<string>> CollectMaskedFieldsAsync(
        IReadOnlyList<string> roles,
        IReadOnlyCollection<string> serviceNames,
        string layerName,
        CancellationToken cancellationToken)
    {
        var maskedFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Always evaluate with the wildcard service so "*"-service policies apply even
        // when the resource has no resolvable publication (e.g. direct layer-id access),
        // plus each concrete service name the resource is published under.
        var lookupServices = new HashSet<string>(serviceNames, StringComparer.OrdinalIgnoreCase) { "*" };

        foreach (var service in lookupServices)
        {
            var policies = await _policyStore
                .GetEffectivePoliciesAsync(roles, service, layerName, cancellationToken)
                .ConfigureAwait(false);
            maskedFields.UnionWith(policies
                .Where(policy => !string.IsNullOrWhiteSpace(policy.Attribute))
                .Select(policy => policy.Attribute.Trim()));
        }

        return maskedFields;
    }

    private async Task<IReadOnlyCollection<string>> ResolveServiceNamesAsync(
        MetadataV2Resource resource,
        CancellationToken cancellationToken)
    {
        try
        {
            var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            var index = snapshot.Index;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var publication in index.PublicationsById.Values)
            {
                if (!string.Equals(publication.ResourceId, resource.Metadata.Id, StringComparison.Ordinal))
                {
                    continue;
                }

                if (index.ServicesById.TryGetValue(publication.ServiceId, out var service) &&
                    !string.IsNullOrWhiteSpace(service.Metadata.Name))
                {
                    names.Add(service.Metadata.Name);
                }
            }

            return names;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Metadata resolution is best-effort for service scoping; "*"-service
            // policies still apply via the wildcard lookup. Never fail the query.
            FieldMaskLog.ServiceResolutionFailed(_logger, resource.Metadata.Name, ex);
            return Array.Empty<string>();
        }
    }

    private static partial class FieldMaskLog
    {
        [LoggerMessage(EventId = 4570, Level = LogLevel.Warning,
            Message = "Field-mask service-name resolution failed for layer '{Layer}'; only wildcard-service policies will apply")]
        public static partial void ServiceResolutionFailed(ILogger logger, string layer, Exception exception);
    }

}
