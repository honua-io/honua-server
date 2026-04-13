// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Configuration;

/// <summary>
/// Configuration options for the centralized secret provider.
/// </summary>
public sealed class SecretProviderOptions
{
    /// <summary>
    /// The configuration section name.
    /// </summary>
    public const string SectionName = "SecretProvider";

    /// <summary>
    /// Gets or sets a value indicating whether resolved secrets should be cached.
    /// </summary>
    public bool EnableCaching { get; set; } = true;

    /// <summary>
    /// Gets or sets the cache duration for resolved secrets.
    /// </summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Gets or sets the maximum number of cached secrets.
    /// </summary>
    public int MaxCacheSize { get; set; } = 256;

    /// <summary>
    /// Gets or sets the timeout for secret resolution operations.
    /// </summary>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Gets or sets a value indicating whether secret access should be logged.
    /// </summary>
    public bool LogSecretAccess { get; set; } = true;
}
