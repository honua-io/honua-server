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
    /// Map template resource kind.
    /// </summary>
    public const string MapTemplate = "MapTemplate";

    /// <summary>
    /// Theme resource kind.
    /// </summary>
    public const string Theme = "Theme";

    /// <summary>
    /// Catalog group resource kind.
    /// </summary>
    public const string Group = "Group";

    /// <summary>
    /// Saved SDK source descriptor resource kind.
    /// </summary>
    public const string SourceDescriptor = "SourceDescriptor";

    /// <summary>
    /// All supported resource kinds.
    /// </summary>
    public static readonly IReadOnlyList<string> All =
    [
        Service,
        Layer,
        Relationship,
        Style,
        Connection,
        MapTemplate,
        Theme,
        Group,
        SourceDescriptor
    ];
}
