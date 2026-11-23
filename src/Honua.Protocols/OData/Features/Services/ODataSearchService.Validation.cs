// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

internal sealed partial class ODataSearchService
{
    internal Task<string?> FindUnsupportedFilterSridAsync(string? filter, CancellationToken cancellationToken)
        => _queryService.FindUnsupportedFilterSridAsync(filter, _crsRegistry, cancellationToken);

    private async Task EnsureFilterSridsSupportedAsync(string? filter, CancellationToken cancellationToken)
    {
        if (await FindUnsupportedFilterSridAsync(filter, cancellationToken).ConfigureAwait(false) is { } error)
        {
            throw new ArgumentException(error);
        }
    }

    // $filter composes with every filter(...) transform of the $apply pipeline.
    private async Task EnsureApplyFilterSridsSupportedAsync(
        IReadOnlyList<string> segments,
        string? filter,
        CancellationToken cancellationToken)
    {
        var applyFilters = segments.Select(ODataAggregationHandler.ParseApplyExpression)
            .Where(parsed => parsed.Type == AggregationType.Filter)
            .Select(parsed => parsed.FilterExpression)
            .Prepend(filter);
        foreach (var applyFilter in applyFilters)
        {
            await EnsureFilterSridsSupportedAsync(applyFilter, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void EnsureNavigationPropertiesExist(
        IReadOnlyDictionary<string, ExpandRelationshipOptions> optionsByName,
        MetadataV2Object resource)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relationship in resource.Relationships)
        {
            known.Add(relationship.Name);
            known.Add(ODataUtilityService.SanitizeIdentifier(relationship.Name));
            known.Add(ODataUtilityService.BuildRelationshipMetadataNameForV2(relationship.Name, relationship.Id));
        }

        var unknown = optionsByName.Keys.FirstOrDefault(name => !known.Contains(name));
        if (unknown is not null)
        {
            throw new ArgumentException($"'{unknown}' is not a navigation property of this layer.");
        }
    }
}
