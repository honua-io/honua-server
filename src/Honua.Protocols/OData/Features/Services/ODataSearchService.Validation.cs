// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

internal readonly record struct ODataQueryResource(MetadataV2Object Resource, HttpContext? Context = null);

internal sealed partial class ODataSearchService
{
    // Match the request-visible publications used to build the shared Feature type in $metadata.
    internal async Task<string?> FindInvalidExpandAsync(string? expand, ODataQueryResource scope, CancellationToken cancellationToken)
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

        var resources = scope.Context is null
            ? [scope.Resource]
            : (await ODataV2Lookups.ResolveVisibleODataPublicationsAsync(scope.Context, cancellationToken).ConfigureAwait(false))
                .Select(publication => publication.Resource).ToArray();
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
