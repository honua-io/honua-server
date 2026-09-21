// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features.FeatureStore.Services;

/// <summary>
/// Resolves the read policy a feature provider enforces for a layer: the metadata-v2
/// permanent filter, the request-scoped row-level security (RLS) predicate (#502) and the
/// request-scoped field masks (#1940). This is the single, provider-neutral implementation
/// shared by every read surface (feature stores, the storage-mapped reader and the spatial
/// analytics reader), so providers cannot drift apart in what they resolve.
/// </summary>
/// <remarks>
/// A provider that applies the resolved policy calls <see cref="ApplyAsync(int, FeatureQuery, CancellationToken)"/>
/// (or the resource-keyed overload). A provider that cannot apply a row-level security
/// predicate or field masks calls <see cref="EnsureNoUnenforcedPolicyAsync"/> on every read
/// path instead, so a policy that targets one of its layers refuses the read rather than
/// being silently skipped.
/// </remarks>
public sealed class LayerReadSecurityResolver
{
    private readonly IMetadataV2GraphProvider? _v2Provider;
    private readonly IFilterExpressionService? _filterExpressionService;
    private readonly IRowLevelSecurityFilterSource? _rlsFilterSource;
    private readonly IFieldMaskSource? _fieldMaskSource;

    /// <summary>
    /// Creates a resolver over the optional metadata, filter and request-scoped policy services.
    /// </summary>
    /// <param name="v2Provider">Metadata v2 graph provider used to map a storage layer id to its resource.</param>
    /// <param name="filterExpressionService">Filter service used to translate the permanent filter.</param>
    /// <param name="rlsFilterSource">Request-scoped row-level security predicate source.</param>
    /// <param name="fieldMaskSource">Request-scoped field mask source.</param>
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

    /// <summary>
    /// Builds a resolver from the services registered in <paramref name="services"/>, so every
    /// provider registration wires the metadata, filter, row-level security and field-mask
    /// services the same way.
    /// </summary>
    /// <param name="services">Scoped service provider of the current registration.</param>
    /// <returns>A resolver over the registered (optional) services.</returns>
    public static LayerReadSecurityResolver FromServices(IServiceProvider services)
    {
        ArgumentNullException.ThrowIfNull(services);

        return new LayerReadSecurityResolver(
            services.GetService<IMetadataV2GraphProvider>(),
            services.GetService<IFilterExpressionService>(),
            services.GetService<IRowLevelSecurityFilterSource>(),
            services.GetService<IFieldMaskSource>());
    }

    /// <summary>
    /// Stamps the layer's enforced row filter and masked fields onto <paramref name="query"/>
    /// and rejects query expressions that reference a masked field.
    /// </summary>
    /// <param name="layerId">Storage layer id being read.</param>
    /// <param name="query">Query to stamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query carrying the enforced filter and masked fields.</returns>
    public async Task<FeatureQuery> ApplyAsync(
        int layerId,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Resource-keyed variant of <see cref="ApplyAsync(int, FeatureQuery, CancellationToken)"/> for
    /// readers that are already bound to a metadata resource and need no layer-id lookup.
    /// </summary>
    /// <param name="resource">Resource being read.</param>
    /// <param name="query">Query to stamp.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The query carrying the enforced filter and masked fields.</returns>
    public async Task<FeatureQuery> ApplyAsync(
        MetadataV2Resource resource,
        FeatureQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(resource);

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

    /// <summary>
    /// Resolves the request-scoped field-level-security (column masking) set (#1940) for
    /// the layer, or an empty set when no masking applies (no policy, no request context).
    /// Best-effort metadata lookup so a missing resource never throws here; the field-mask
    /// source itself returns an empty set when nothing matches.
    /// </summary>
    /// <param name="layerId">Storage layer id being read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The masked field names, or an empty set.</returns>
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

    /// <summary>
    /// Resolves the layer's enforced (row-visibility) filter to a parameterized SQL
    /// fragment, or null when no filter applies. Combines two independent sources with
    /// AND so both are honored on every read surface:
    /// <list type="bullet">
    ///   <item>the layer's metadata-v2 <em>permanent filter</em> (server-declared,
    ///   always-on), and</item>
    ///   <item>the request-scoped <em>row-level security (RLS)</em> predicate (#502),
    ///   derived from the caller's roles/claims and the layer's RLS policies.</item>
    /// </list>
    /// Both fragments are independently parameterized; RLS placeholders are renumbered
    /// so the merged fragment stays positionally consistent for the provider.
    /// </summary>
    /// <param name="layerId">Storage layer id being read.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The combined enforced filter, or null.</returns>
    public async Task<SqlFragment?> ResolveEnforcedSqlFilterAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        var resource = await FindResourceAsync(layerId, cancellationToken).ConfigureAwait(false);
        return resource is null
            ? null
            : await ResolveEnforcedSqlFilterAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses the read when a row-level security predicate or a field mask resolves for the
    /// layer and the calling provider cannot apply it. Providers that do not translate the
    /// enforced row filter and do not mask attributes call this on every read path, so a
    /// policy is never silently skipped. Returns without effect when nothing resolves.
    /// </summary>
    /// <param name="providerDisplayName">Provider name used in the refusal message.</param>
    /// <param name="layerId">Storage layer id being read.</param>
    /// <param name="boundResource">Resource the reader is bound to, when it has one; otherwise
    /// the resource is looked up by <paramref name="layerId"/>.</param>
    /// <param name="rejectPermanentFilter"><see langword="true"/> when the provider does not
    /// translate the layer's metadata-v2 permanent filter either, so a configured permanent
    /// filter refuses the read as well.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="NotSupportedException">A permanent filter, row-level security policy
    /// or field-mask policy applies and the provider cannot enforce it.</exception>
    public async Task EnsureNoUnenforcedPolicyAsync(
        string providerDisplayName,
        int layerId,
        MetadataV2Resource? boundResource,
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

    private async Task<ImmutableArray<string>> ResolveMaskedFieldsAsync(
        MetadataV2Resource resource,
        CancellationToken cancellationToken)
    {
        return _fieldMaskSource is null
            ? ImmutableArray<string>.Empty
            : await _fieldMaskSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SqlFragment?> ResolveEnforcedSqlFilterAsync(
        MetadataV2Resource resource,
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

    /// <summary>
    /// Best-effort storage-layer-id to resource lookup: a missing graph provider or a layer
    /// id absent from the metadata index resolves to no resource (and therefore no policy).
    /// </summary>
    private async Task<MetadataV2Resource?> FindResourceAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_v2Provider is null)
        {
            return null;
        }

        var snapshot = await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        return snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource) ? resource : null;
    }
}
