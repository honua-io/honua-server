// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Queries.Filters;
using Honua.Infrastructure.Validation;

namespace Honua.Protocols.OData.Services;

internal sealed partial class ODataQueryService
{
    // A geometry literal's SRID reaches SQL unchecked otherwise, and PostGIS answers an unknown
    // one with an internal error. OGC API Features applies the same registry check.
    internal async Task<string?> FindUnsupportedSridErrorAsync(FilterExpression? expression, CancellationToken cancellationToken)
    {
        if (expression is null || _crsRegistry is null)
        {
            return null;
        }

        foreach (var srid in FilterGeometryCrsValidator.GetExplicitGeometrySrids(expression))
        {
            if (!await _crsRegistry.IsSridSupportedAsync(srid, cancellationToken).ConfigureAwait(false))
            {
                return $"Unsupported explicit geometry CRS 'EPSG:{srid}'.";
            }
        }

        return null;
    }

    /// <summary>Throws <see cref="ArgumentException"/> when an OData filter names an unsupported geometry SRID.</summary>
    internal async Task EnsureFilterSridsSupportedAsync(string? odataFilter, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(odataFilter))
        {
            return;
        }

        var parsed = _filterExpressionService.Parse(FilterLanguage.OData, odataFilter);
        if (parsed.IsSuccess && await FindUnsupportedSridErrorAsync(parsed.Expression, cancellationToken).ConfigureAwait(false) is { } error)
        {
            throw new ArgumentException(error);
        }
    }
}
