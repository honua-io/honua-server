// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Metadata.Domain;

/// <summary>
/// Known metadata resource kinds supported by the schema registry.
/// </summary>
public static class MetadataResourceKinds
{
    /// <summary>
    /// Service resource kind.
    /// </summary>
    public const string Service = "Service";

    /// <summary>
    /// Layer resource kind.
    /// </summary>
    public const string Layer = "Layer";

    /// <summary>
    /// Relationship resource kind.
    /// </summary>
    public const string Relationship = "Relationship";

    /// <summary>
    /// Style resource kind.
    /// </summary>
    public const string Style = "Style";

    /// <summary>
    /// Connection resource kind.
    /// </summary>
    public const string Connection = "Connection";

    /// <summary>
    /// All supported resource kinds.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        Service,
        Layer,
        Relationship,
        Style,
        Connection
    ];
}
