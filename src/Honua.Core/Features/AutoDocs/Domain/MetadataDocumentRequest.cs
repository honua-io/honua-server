// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.AutoDocs.Domain;

/// <summary>
/// Request to generate metadata documents for a metadata v2 resource.
/// </summary>
/// <param name="Resource">The resource providing schema metadata.</param>
/// <param name="ServiceName">The parent service name.</param>
/// <param name="OrganizationName">Organization responsible for the data.</param>
/// <param name="ContactEmail">Contact email for metadata inquiries.</param>
/// <param name="Abstract">A textual summary of the dataset.</param>
/// <param name="Purpose">The intended purpose of the dataset.</param>
/// <param name="Keywords">Keywords for discovery.</param>
/// <param name="AccessConstraints">Access or use constraints.</param>
/// <param name="UpdateFrequency">How often the data is updated.</param>
public sealed record MetadataDocumentRequest(
    MetadataV2Resource Resource,
    string ServiceName,
    string? OrganizationName = null,
    string? ContactEmail = null,
    string? Abstract = null,
    string? Purpose = null,
    IReadOnlyList<string>? Keywords = null,
    string? AccessConstraints = null,
    string? UpdateFrequency = null)
{
    /// <summary>
    /// Resource name suitable for stable identifiers and headings.
    /// </summary>
    public string ResourceName =>
        string.IsNullOrWhiteSpace(Resource.Metadata.Name)
            ? Resource.Metadata.Id
            : Resource.Metadata.Name;

    /// <summary>
    /// Human-readable resource description.
    /// </summary>
    public string? ResourceDescription => Resource.Metadata.Description;
}
