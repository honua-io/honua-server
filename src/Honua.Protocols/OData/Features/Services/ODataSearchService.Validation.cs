// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;
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
            throw new ValidationException(error);
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

    // $metadata declares every resource's relationships on the shared Feature type, so a name is
    // valid when any resource declares it; "*" keeps its earlier pass-through.
    internal async Task<string?> FindInvalidExpandAsync(string? expand, MetadataV2Object resource, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expand))
        {
            return null;
        }

        Dictionary<string, ExpandRelationshipOptions> requested;
        try
        {
            requested = ParseExpandRelationshipOptions(expand);
        }
        catch (ArgumentException exception)
        {
            return exception.Message;
        }

        var snapshot = await _graphProvider.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
        var resources = (snapshot?.Graph.Resources ?? []).Append(resource);
        var known = resources.SelectMany(NavigationPropertyNames).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var unknown = requested.Keys.FirstOrDefault(name => name != "*" && !known.Contains(name));
        return unknown is null ? null : $"Invalid $expand query option: '{unknown}' is not a navigation property.";
    }

    private static IEnumerable<string> NavigationPropertyNames(MetadataV2Object resource)
        => resource.Relationships.SelectMany(relationship => new[]
        {
            relationship.Name,
            ODataUtilityService.SanitizeIdentifier(relationship.Name),
            ODataUtilityService.BuildRelationshipMetadataNameForV2(relationship.Name, relationship.Id),
        });

    // A schema navigation property this layer does not declare is still part of the entity shape.
    private static void AddUndeclaredExpansions(
        Dictionary<long, Dictionary<string, object?[]>> result,
        IEnumerable<string> undeclared,
        long[] objectIds)
    {
        foreach (var name in undeclared.Where(name => name != "*"))
        {
            foreach (var objectId in objectIds)
            {
                if (!result.TryGetValue(objectId, out var relations))
                {
                    relations = [];
                    result[objectId] = relations;
                }

                relations.TryAdd(name, []);
            }
        }
    }
}
