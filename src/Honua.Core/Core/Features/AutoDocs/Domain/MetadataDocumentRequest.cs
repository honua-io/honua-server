// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.AutoDocs.Domain;

public sealed record MetadataDocumentRequest(
    MetadataV2Object Resource,
    string ServiceName,
    string? OrganizationName = null,
    string? ContactEmail = null,
    string? Abstract = null,
    string? Purpose = null,
    IReadOnlyList<string>? Keywords = null,
    string? AccessConstraints = null,
    string? UpdateFrequency = null)
{
    public string ResourceName =>
        string.IsNullOrWhiteSpace(Resource.Metadata.Name)
            ? Resource.Metadata.Id
            : Resource.Metadata.Name;

    public string? ResourceDescription => Resource.Metadata.Description;
}
