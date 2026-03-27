// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Abstractions;

/// <summary>
/// Checks whether the database supports H3 operations via the h3-pg extension.
/// </summary>
public interface IH3CapabilityChecker
{
    /// <summary>
    /// Returns true if the h3-pg extension is installed and available for queries,
    /// false if the extension is confirmed absent, or null if the check failed
    /// due to a transient error (e.g. database unreachable).
    /// The result may be cached for the lifetime of the application.
    /// </summary>
    Task<bool?> IsH3AvailableAsync(CancellationToken cancellationToken = default);
}
