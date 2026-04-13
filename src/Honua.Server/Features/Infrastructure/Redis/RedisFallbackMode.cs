// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.Redis;

/// <summary>
/// Defines the fallback behavior when Redis is unavailable.
/// </summary>
public enum RedisFallbackMode
{
    /// <summary>
    /// Fail immediately when Redis is unavailable.
    /// No fallback behavior - operations will throw exceptions.
    /// </summary>
    FailFast = 0,

    /// <summary>
    /// Fall back to in-memory implementations when Redis is unavailable.
    /// Suitable for non-critical background operations.
    /// </summary>
    InMemoryFallback = 1,

    /// <summary>
    /// Allow local fallback in development environments.
    /// Falls back to in-memory in dev, fails fast in production.
    /// </summary>
    AllowLocalInDev = 2
}