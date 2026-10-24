// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Core.Features.Infrastructure.Abstractions;

/// <summary>
/// Checks database compatibility at startup (extensions, versions, capabilities).
/// </summary>
public interface IDatabaseCompatibilityChecker
{
    /// <summary>
    /// Checks whether the target database meets Honua compatibility requirements.
    /// </summary>
    /// <param name="connectionString">Connection string for the target database.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Compatibility check result with version and extension details.</returns>
    Task<DatabaseCompatibilityResult> CheckCompatibilityAsync(
        string connectionString, CancellationToken cancellationToken = default);
}
