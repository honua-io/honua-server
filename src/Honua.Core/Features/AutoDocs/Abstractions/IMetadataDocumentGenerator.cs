// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AutoDocs.Domain;

namespace Honua.Core.Features.AutoDocs.Abstractions;

/// <summary>
/// Generates standards-compliant metadata documents from layer schema and metadata.
/// All output is deterministic and template-driven (no LLM dependency).
/// </summary>
public interface IMetadataDocumentGenerator
{
    /// <summary>
    /// Generates ISO 19115 XML, FGDC XML, and a data dictionary from the provided metadata.
    /// </summary>
    /// <param name="request">The document generation request with layer and metadata context.</param>
    /// <returns>The generated metadata documents.</returns>
    MetadataDocumentResult Generate(MetadataDocumentRequest request);
}
