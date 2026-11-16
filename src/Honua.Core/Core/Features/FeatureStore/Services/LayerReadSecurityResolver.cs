// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Exceptions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.FeatureStore.Services;

public sealed class LayerReadSecurityResolver
{
    private readonly IMetadataV2GraphProvider? _v2Provider;
    private readonly IFilterExpressionService? _filterExpressionService;
    private readonly IRowLevelSecurityFilterSource? _rlsFilterSource;
    private readonly IFieldMaskSource? _fieldMaskSource;

    public LayerReadSecurityResolver(
        IMetadataV2GraphProvider? v2Provider,
        IFilterExpressionService? filterExpressionService,
        IRowLevelSecurityFilterSource? rlsFilterSource,
        IFieldMaskSource? fieldMaskSource)
    {
        _v2Provider = v2Provider;
        _filterExpressionService = filterExpressionService;
        _rlsFilterSource = rlsFilterSource;
        _fieldMaskSource = fieldMaskSource;
    }

    public static LayerReadSecurityResolver FromServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return new LayerReadSecurityResolver(
            services.GetService<IMetadataV2GraphProvider>(),
            services.GetService<IFilterExpressionService>(),
            services.GetService<IRowLevelSecurityFilterSource>(),
            services.GetService<IFieldMaskSource>());
    }

    public async Task<FeatureQuery> ApplyAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        if (CanResolveCombined(query))
        {
            var resource = await FindResourceAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (resource is not null)
            {
                return await ApplyAsync(resource, query, cancellationToken).ConfigureAwait(false);
            }
        }

        // Resolve each concern independently. A nested caller may already carry one
        // enforced value, but that must not suppress resolution of the other concern.
        if (query.EnforcedSqlFilter is null)
        {
            var enforcedFilter = await ResolveEnforcedSqlFilterAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (enforcedFilter is not null)
            {
                query = query with { EnforcedSqlFilter = enforcedFilter };
            }
        }

        if (query.EnforcedMaskedFields is null)
        {
            var maskedFields = await ResolveMaskedFieldsAsync(layerId, cancellationToken).ConfigureAwait(false);
            if (!maskedFields.IsDefaultOrEmpty)
            {
                query = query with { EnforcedMaskedFields = maskedFields };
            }
        }

        FeatureQuerySecurity.Validate(query);
        return query;
    }

    public async Task<FeatureQuery> ApplyAsync(
        MetadataV2Object resource,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

        if (CanResolveCombined(query))
        {
            var permanentFilter = PermanentFilterResolver.Resolve(resource, _filterExpressionService);
            var combined = await ((ICombinedReadSecuritySource)_rlsFilterSource!)
                .ResolveCombinedAsync(resource, _fieldMaskSource!, cancellationToken)
                .ConfigureAwait(false);
            var filter = SqlFragmentHelpers.CombineSqlFilters(permanentFilter, combined.RowFilter);
            if (filter is not null)
            {
                query = query with { EnforcedSqlFilter = filter };
            }

            if (!combined.MaskedFields.IsDefaultOrEmpty)
            {
                query = query with { EnforcedMaskedFields = combined.MaskedFields };
            }

            FeatureQuerySecurity.Validate(query);
            return query;
        }

        if (query.EnforcedSqlFilter is null)
        {
            var enforcedFilter = await ResolveEnforcedSqlFilterAsync(resource, cancellationToken).ConfigureAwait(false);
            if (enforcedFilter is not null)
            {
                query = query with { EnforcedSqlFilter = enforcedFilter };
            }
        }

        if (query.EnforcedMaskedFields is null)
        {
            var maskedFields = await ResolveMaskedFieldsAsync(resource, cancellationToken).ConfigureAwait(false);
            if (!maskedFields.IsDefaultOrEmpty)
            {
                query = query with { EnforcedMaskedFields = maskedFields };
            }
        }

        FeatureQuerySecurity.Validate(query);
        return query;
    }

    public async Task<ImmutableArray<string>> ResolveMaskedFieldsAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_fieldMaskSource is null)
        {
            return ImmutableArray<string>.Empty;
        }

        var resource = await FindResourceAsync(layerId, cancellationToken).ConfigureAwait(false);
        return resource is null
            ? ImmutableArray<string>.Empty
            : await ResolveMaskedFieldsAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SqlFragment?> ResolveEnforcedSqlFilterAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        var resource = await FindResourceAsync(layerId, cancellationToken).ConfigureAwait(false);
        return resource is null
            ? null
            : await ResolveEnforcedSqlFilterAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    public async Task<SqlFragment?> ResolveRowLevelSecurityFilterAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        var resource = await FindResourceAsync(layerId, cancellationToken).ConfigureAwait(false);
        return resource is null || _rlsFilterSource is null
            ? null
            : await _rlsFilterSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnsureNoUnenforcedPolicyAsync(
        string providerDisplayName,
        int layerId,
        MetadataV2Object? boundResource,
        bool rejectPermanentFilter,
        CancellationToken cancellationToken)
    {
        if (!rejectPermanentFilter && _rlsFilterSource is null && _fieldMaskSource is null)
        {
            return;
        }

        var resource = boundResource ?? await FindResourceAsync(layerId, cancellationToken).ConfigureAwait(false);
        if (resource is null)
        {
            return;
        }

        if (rejectPermanentFilter && !string.IsNullOrWhiteSpace(resource.PermanentFilter?.Expression))
        {
            throw new NotSupportedException(
                $"Layer {layerId} has a permanent (row-visibility) filter configured, but the {providerDisplayName} provider cannot enforce it: " +
                "permanent filters are not translated for this provider. " +
                "Configure permanent filters only on providers that apply them, or remove the permanent filter from this layer.");
        }

        if (_rlsFilterSource is not null)
        {
            var rlsFilter = await _rlsFilterSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
            if (rlsFilter is not null)
            {
                throw new NotSupportedException(
                    $"Layer {layerId} has a row-level security policy that applies to this request, but the {providerDisplayName} provider cannot enforce it: " +
                    "row-level security predicates are only applied by the Postgres provider. " +
                    "Configure row-level security policies only on Postgres layers, or remove the policy that targets this layer.");
            }
        }

        var maskedFields = await ResolveMaskedFieldsAsync(resource, cancellationToken).ConfigureAwait(false);
        if (!maskedFields.IsDefaultOrEmpty)
        {
            throw new NotSupportedException(
                $"Layer {layerId} has a field-mask policy that applies to this request, but the {providerDisplayName} provider cannot enforce it: " +
                "field masks are only applied by the Postgres provider. " +
                "Configure field-mask policies only on Postgres layers, or remove the policy that targets this layer.");
        }
    }

    private bool CanResolveCombined(FeatureQuery query)
        => query.EnforcedSqlFilter is null
           && query.EnforcedMaskedFields is null
           && _rlsFilterSource is ICombinedReadSecuritySource combined
           && _fieldMaskSource is not null
           && combined.CanResolveWith(_fieldMaskSource);

    private async Task<ImmutableArray<string>> ResolveMaskedFieldsAsync(
        MetadataV2Object resource,
        CancellationToken cancellationToken)
    {
        return _fieldMaskSource is null
            ? ImmutableArray<string>.Empty
            : await _fieldMaskSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqlFragment?> ResolveEnforcedSqlFilterAsync(
        MetadataV2Object resource,
        CancellationToken cancellationToken)
    {
        var permanentFilter = PermanentFilterResolver.Resolve(resource, _filterExpressionService);

        // The RLS source returns null when no policy applies (or there is no request
        // context) and fails secure itself when a policy cannot be translated.
        var rlsFilter = _rlsFilterSource is null
            ? null
            : await _rlsFilterSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);

        // The shared helper shifts the right-hand fragment's @pN / $N placeholders past the
        // permanent filter's parameters so the merged parameter list lines up positionally.
        return SqlFragmentHelpers.CombineSqlFilters(permanentFilter, rlsFilter);
    }

    public static SqlFragment? ResolvePermanentFilter(
        MetadataV2Object resource,
        IFilterExpressionService? filterExpressionService)
        => PermanentFilterResolver.Resolve(resource, filterExpressionService);

    public static FilterExpression? RequirePermanentFilterExpression(
        MetadataV2Object resource,
        IFilterExpressionService? filterExpressionService)
        => PermanentFilterResolver.ParseSavedExpression(resource, filterExpressionService);

    private async Task<MetadataV2Object?> FindResourceAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_v2Provider is null)
        {
            return null;
        }

        var snapshot = await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var candidates = FindResourceCandidates(snapshot, layerId);
        if (candidates.Length > 1)
        {
            await RejectAmbiguousPolicyAsync(candidates, cancellationToken).ConfigureAwait(false);
        }
        return candidates.FirstOrDefault();
    }

    private static MetadataV2Object[] FindResourceCandidates(MetadataV2GraphSnapshot snapshot, int layerId)
    {
        var boundResources = snapshot.Graph.StorageBindings
            .Where(binding => binding.StorageLayerId == layerId)
            .Select(binding => snapshot.Index.ResourcesById.GetValueOrDefault(binding.ResourceId))
            .OfType<MetadataV2Object>();
        var publishedResources = snapshot.Graph.Publications
            .Select(publication => (Publication: publication, Resource: snapshot.ResolveResource(publication)))
            .Where(candidate => candidate.Resource is not null &&
                snapshot.ResolveStorageLayerId(candidate.Publication, candidate.Resource) == layerId)
            .Select(candidate => candidate.Resource!);
        var candidates = boundResources.Concat(publishedResources)
            .DistinctBy(candidate => candidate.Metadata.Id, StringComparer.Ordinal)
            .ToArray();
        var featureResources = candidates.Where(IsFeatureResource).ToArray();
        if (featureResources.Length == 0 || featureResources.Length == candidates.Length)
        {
            return candidates;
        }

        var bindings = snapshot.Graph.StorageBindings.Where(binding => binding.StorageLayerId == layerId)
            .ToLookup(binding => binding.ResourceId, StringComparer.Ordinal);
        if (featureResources.Any(resource =>
            !bindings[resource.Metadata.Id].Any() || bindings[resource.Metadata.Id]
                .Any(binding => binding.StorageType != MetadataV2StorageType.RelationalTable)))
        {
            return candidates;
        }

        // Numeric handles cannot distinguish schemas or connection aliases; matching table names remain ambiguous.
        var featureTableNames = featureResources.SelectMany(resource => bindings[resource.Metadata.Id]
                .Select(binding => FeatureStorageMapping.FromMetadata(resource, binding).TableName))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates.Where(resource => IsFeatureResource(resource) || !UsesDistinctTables(resource)).ToArray();

        bool UsesDistinctTables(MetadataV2Object resource)
        {
            var resourceBindings = bindings[resource.Metadata.Id].ToArray();
            try
            {
                return resourceBindings.Length > 0 && resourceBindings.All(binding =>
                    binding.StorageType == MetadataV2StorageType.RelationalTable &&
                    !featureTableNames.Contains(FeatureStorageMapping.FromMetadata(resource, binding).TableName));
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    private static bool IsFeatureResource(MetadataV2Object resource)
        => resource.Type is MetadataV2ObjectType.FeatureDataset or MetadataV2ObjectType.Table;

    private async Task RejectAmbiguousPolicyAsync(
        IReadOnlyList<MetadataV2Object> resources, CancellationToken cancellationToken)
    {
        foreach (var resource in resources)
        {
            if (!string.IsNullOrWhiteSpace(resource.PermanentFilter?.Expression) ||
                (_rlsFilterSource is not null && await _rlsFilterSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false) is not null) ||
                !(await ResolveMaskedFieldsAsync(resource, cancellationToken).ConfigureAwait(false)).IsDefaultOrEmpty)
            {
                throw new ResourceAccessDeniedException();
            }
        }
    }

}
