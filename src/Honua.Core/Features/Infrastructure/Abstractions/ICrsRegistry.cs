// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Shared.Models;

namespace Honua.Core.Features.Infrastructure.Abstractions;

public interface ICrsRegistry
{
    ValueTask<CrsDefinition?> ResolveAsync(string? crsIdentifier, CancellationToken cancellationToken = default);

    ValueTask<CrsDefinition?> ResolveBySridAsync(int srid, CancellationToken cancellationToken = default);

    ValueTask<CoordinateSystemSearchResult> SearchAsync(string query, int limit, CancellationToken cancellationToken = default)
        => ValueTask.FromException<CoordinateSystemSearchResult>(new NotSupportedException("The coordinate-system catalog is unavailable."));

    ValueTask<bool> IsSridSupportedAsync(int srid, CancellationToken cancellationToken = default);
}
