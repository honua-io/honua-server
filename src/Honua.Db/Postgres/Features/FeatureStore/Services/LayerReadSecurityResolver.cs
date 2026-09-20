// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Queries.Filters;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

/// <summary>
/// Resolves the read policy the PostgreSQL provider enforces for a storage layer: the
/// metadata-v2 permanent filter, the request-scoped row-level security (RLS) predicate
/// (#502) and the request-scoped field masks (#1940). This is the single implementation
/// shared by every layer-id keyed read surface of the provider (the feature store and the
/// spatial analytics reader), so they cannot drift apart in what they enforce.
/// </summary>
internal sealed class LayerReadSecurityResolver
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

    /// <summary>
    /// Stamps the layer's enforced row filter and masked fields onto <paramref name="query"/>
    /// and rejects query expressions that reference a masked field.
    /// </summary>
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
    /// Resolves the request-scoped field-level-security (column masking) set (#1940) for
    /// the layer, or an empty set when no masking applies (no policy, no request context).
    /// Best-effort metadata lookup so a missing resource never throws here; the field-mask
    /// source itself returns an empty set when nothing matches.
    /// </summary>
    public async Task<ImmutableArray<string>> ResolveMaskedFieldsAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_fieldMaskSource is null || _v2Provider is null)
        {
            return ImmutableArray<string>.Empty;
        }

        var snapshot = await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource))
        {
            return ImmutableArray<string>.Empty;
        }

        return await _fieldMaskSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
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
    public async Task<SqlFragment?> ResolveEnforcedSqlFilterAsync(
        int layerId,
        CancellationToken cancellationToken)
    {
        var permanentFilter = await PermanentFilterResolver
            .ResolveAsync(_v2Provider, _filterExpressionService, layerId, cancellationToken)
            .ConfigureAwait(false);

        var rlsFilter = await ResolveRlsFilterAsync(layerId, cancellationToken).ConfigureAwait(false);

        return CombineEnforcedFilters(permanentFilter, rlsFilter);
    }

    /// <summary>
    /// Resolves the request-scoped RLS predicate for the layer, or null when no RLS
    /// applies (no policy, or no request context). Best-effort metadata lookup so a
    /// missing resource never throws here; the RLS source itself fails secure.
    /// </summary>
    private async Task<SqlFragment?> ResolveRlsFilterAsync(int layerId, CancellationToken cancellationToken)
    {
        if (_rlsFilterSource is null || _v2Provider is null)
        {
            return null;
        }

        var snapshot = await _v2Provider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!snapshot.Index.ResourcesByStorageLayerId.TryGetValue(layerId, out var resource))
        {
            return null;
        }

        return await _rlsFilterSource.ResolveAsync(resource, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// AND-combines the permanent filter and RLS fragments. Either may be null.
    /// The RLS fragment's <c>@pN</c> placeholders are shifted past the permanent
    /// filter's parameters so the merged parameter list lines up positionally.
    /// </summary>
    private static SqlFragment? CombineEnforcedFilters(SqlFragment? permanentFilter, SqlFragment? rlsFilter)
    {
        if (permanentFilter is null)
        {
            return rlsFilter;
        }

        if (rlsFilter is null)
        {
            return permanentFilter;
        }

        var offset = permanentFilter.Parameters.Count;
        var shiftedRlsSql = ShiftNamedParameters(rlsFilter.Sql, offset);
        var parameters = new List<object?>(permanentFilter.Parameters);
        parameters.AddRange(rlsFilter.Parameters);
        return new SqlFragment($"({permanentFilter.Sql}) AND ({shiftedRlsSql})", parameters);
    }

    private static string ShiftNamedParameters(string sql, int offset)
    {
        if (offset == 0)
        {
            return sql;
        }

        return System.Text.RegularExpressions.Regex.Replace(
            sql,
            @"@p(\d+)",
            match =>
            {
                var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                return $"@p{index + offset}";
            },
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    }
}
