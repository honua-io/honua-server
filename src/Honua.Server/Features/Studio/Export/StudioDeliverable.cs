// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Studio.Domain;

namespace Honua.Server.Features.Studio.Export;

/// <summary>
/// Supported export formats for a Studio deliverable.
/// </summary>
public enum StudioDeliverableFormat
{
    /// <summary>Portable Network Graphics raster.</summary>
    Png,

    /// <summary>Portable Document Format.</summary>
    Pdf,
}

/// <summary>
/// A rendered Studio deliverable artifact (map, dashboard, or report exported to PDF/PNG).
/// </summary>
public sealed record StudioDeliverableArtifact
{
    /// <summary>Encoded artifact bytes.</summary>
    public required byte[] Content { get; init; }

    /// <summary>MIME content type of <see cref="Content"/>.</summary>
    public required string ContentType { get; init; }

    /// <summary>Suggested download file name including extension.</summary>
    public required string FileName { get; init; }

    /// <summary>Studio package family the artifact was rendered from.</summary>
    public required StudioPackageFamily Family { get; init; }

    /// <summary>Export format of the artifact.</summary>
    public required StudioDeliverableFormat Format { get; init; }
}
