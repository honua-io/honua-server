// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Queries.Filters.OData;
using Honua.Hosting.Features.Validation;

namespace Honua.Protocols.OData.Services;

// An explicit literal SRID the store does not know would otherwise reach SQL, where PostGIS
// fails it ("Invalid reserved SRID") and the caller gets a 500. OGC API Features runs the same check.
internal static class ODataGeometrySridGuard
{
    public static async Task<string?> FindUnsupportedSridErrorAsync(
        ICrsRegistry crsRegistry,
        IEnumerable<string?> odataFilters,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(crsRegistry);
        ArgumentNullException.ThrowIfNull(odataFilters);

        foreach (var srid in odataFilters.SelectMany(ExplicitGeometrySrids).Distinct())
        {
            if (!await crsRegistry.IsSridSupportedAsync(srid, cancellationToken).ConfigureAwait(false))
            {
                return $"Unsupported geometry literal SRID '{srid.ToString(CultureInfo.InvariantCulture)}'.";
            }
        }

        return null;
    }

    private static IEnumerable<int> ExplicitGeometrySrids(string? odataFilter)
    {
        if (string.IsNullOrWhiteSpace(odataFilter))
        {
            return [];
        }

        try
        {
            return FilterGeometryCrsValidator.GetExplicitGeometrySrids(new ODataFilterParser().Parse(odataFilter));
        }
        catch (ArgumentException)
        {
            // A malformed filter is reported, with its position, by the normal parse path.
            return [];
        }
    }
}
