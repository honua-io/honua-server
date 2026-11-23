// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Validation;

namespace Honua.Protocols.OData.Services;

internal sealed partial class ODataQueryService
{
    // A geometry literal's SRID reaches SQL unchecked otherwise, and PostGIS answers an unknown
    // one with an internal error. OGC API Features applies the same registry check. A filter that
    // does not parse is left to the regular query path, which reports the syntax error.
    internal async Task<string?> FindUnsupportedFilterSridAsync(
        string? odataFilter,
        ICrsRegistry crsRegistry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(odataFilter))
        {
            return null;
        }

        var parsed = _filterExpressionService.Parse(FilterLanguage.OData, odataFilter);
        if (!parsed.IsSuccess || parsed.Expression is null)
        {
            return null;
        }

        foreach (var srid in FilterGeometryCrsValidator.GetExplicitGeometrySrids(parsed.Expression))
        {
            if (!await crsRegistry.IsSridSupportedAsync(srid, cancellationToken).ConfigureAwait(false))
            {
                return $"Unsupported explicit geometry CRS 'EPSG:{srid}'.";
            }
        }

        return null;
    }
}
