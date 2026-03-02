// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Grpc.Models;

/// <summary>
/// A single geographic feature.
/// </summary>
public sealed class Feature
{
    /// <summary>Feature ID.</summary>
    public long Id { get; init; }

    /// <summary>Feature attributes as key-value pairs.</summary>
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();

    /// <summary>Feature geometry as Esri JSON dictionary.</summary>
    public IReadOnlyDictionary<string, object?>? Geometry { get; init; }
}
