// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Admin.Abstractions;

/// <summary>A read-only plan. The layer ID is a placeholder, never an allocation or reservation.</summary>
public sealed record LayerPublicationPreview(
    LayerPublicationBase MetadataBase,
    MetadataV2Service Service,
    MetadataV2Object Resource,
    MetadataV2StorageBinding StorageBinding,
    IReadOnlyList<MetadataV2Layer> Publications);

public interface ILayerPublicationPreviewReader
{
    Task<LayerPublicationPreview> PreviewPublicationAsync(
        string connectionString, LayerPublishRequest request, CancellationToken cancellationToken = default);
}
