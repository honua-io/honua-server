// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Abstractions;

public interface IGeoServicesParityFeatureReader
{
    /// <summary>
    /// Returns the feature count for the supplied target resource id.
    /// </summary>
    /// <param name="targetResourceId">Stable manifest target resource id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Feature count, or <c>null</c> when the target resource was not found.</returns>
    Task<long?> GetFeatureCountAsync(string targetResourceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the field names advertised by the supplied target resource.
    /// </summary>
    /// <param name="targetResourceId">Stable manifest target resource id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// Field names. Returns an empty array when the target advertises no schema; returns
    /// <c>null</c> when the target resource was not found.
    /// </returns>
    Task<IReadOnlyList<string>?> GetFieldNamesAsync(string targetResourceId, CancellationToken cancellationToken = default);
}
