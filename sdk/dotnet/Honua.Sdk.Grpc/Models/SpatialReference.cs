// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Grpc.Models;

/// <summary>
/// Identifies a coordinate system.
/// </summary>
public sealed class SpatialReference
{
    /// <summary>Well-known ID.</summary>
    public int Wkid { get; init; }

    /// <summary>Latest well-known ID.</summary>
    public int LatestWkid { get; init; }

    /// <summary>Well-known text.</summary>
    public string Wkt { get; init; } = string.Empty;
}
