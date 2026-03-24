// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.AutoDocs.Domain;

/// <summary>
/// Result of metadata document generation, containing all requested output formats.
/// </summary>
public sealed class MetadataDocumentResult
{
    /// <summary>
    /// ISO 19115 XML content.
    /// </summary>
    public required string Iso19115Xml { get; init; }

    /// <summary>
    /// FGDC metadata XML content.
    /// </summary>
    public required string FgdcXml { get; init; }

    /// <summary>
    /// Human-readable data dictionary content (Markdown format).
    /// </summary>
    public required string DataDictionary { get; init; }

    /// <summary>
    /// Timestamp when the documents were generated.
    /// </summary>
    public DateTimeOffset GeneratedAt { get; init; } = DateTimeOffset.UtcNow;
}
