// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Cache directives surfaced to clients and shared caches when serving the
/// hosted scene tileset and asset payloads.
/// </summary>
/// <param name="MaxAgeSeconds">
/// Recommended <c>max-age</c> value (seconds) for the <c>Cache-Control</c>
/// header. Must be 0–86400 inclusive.
/// </param>
/// <param name="NoStore">
/// When true, downstream caches must not store the response. Used for
/// frequently rotated debug/preview datasets.
/// </param>
public sealed record SceneCachePolicy(int MaxAgeSeconds, bool NoStore)
{
    /// <summary>
    /// Default 1-hour public cache policy applied when callers do not specify one.
    /// </summary>
    public static readonly SceneCachePolicy Default = new(3600, false);
}
