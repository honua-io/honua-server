// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.AutoDocs.Domain;

/// <summary>
/// Request to generate metadata documents for a layer.
/// </summary>
/// <param name="Layer">The layer definition providing schema metadata.</param>
/// <param name="ServiceName">The parent service name.</param>
/// <param name="OrganizationName">Organization responsible for the data.</param>
/// <param name="ContactEmail">Contact email for metadata inquiries.</param>
/// <param name="Abstract">A textual summary of the dataset.</param>
/// <param name="Purpose">The intended purpose of the dataset.</param>
/// <param name="Keywords">Keywords for discovery.</param>
/// <param name="AccessConstraints">Access or use constraints.</param>
/// <param name="UpdateFrequency">How often the data is updated.</param>
public sealed record MetadataDocumentRequest(
    LayerDefinition Layer,
    string ServiceName,
    string? OrganizationName = null,
    string? ContactEmail = null,
    string? Abstract = null,
    string? Purpose = null,
    IReadOnlyList<string>? Keywords = null,
    string? AccessConstraints = null,
    string? UpdateFrequency = null);
