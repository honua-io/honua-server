// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Stores the result of the PostGIS preflight compatibility check for use by deploy probes.
/// Written once at startup; safe for single-writer volatile reads.
/// </summary>
internal sealed class DatabaseCompatibilityState
{
    private volatile DatabaseCompatibilityResult? _result;

    /// <summary>
    /// The compatibility check result, or null if the check has not run.
    /// </summary>
    public DatabaseCompatibilityResult? Result => _result;

    /// <summary>
    /// Whether a result is available.
    /// </summary>
    public bool HasResult => _result is not null;

    /// <summary>
    /// Records the compatibility check result.
    /// </summary>
    public void SetResult(DatabaseCompatibilityResult result)
    {
        _result = result ?? throw new ArgumentNullException(nameof(result));
    }
}
