// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Admin.Abstractions;

// A read-only plan. The layer ID is a placeholder, never an allocation or reservation.
public sealed record LayerPublicationPreview
{
    public required LayerPublicationBase MetadataBase { get; init; }
    public required MetadataV2Service Service { get; init; }
    public required MetadataV2Object Resource { get; init; }
    public required MetadataV2StorageBinding StorageBinding { get; init; }
    public required IReadOnlyList<MetadataV2Layer> Publications { get; init; }
}

public interface ILayerPublicationPreviewReader
{
    Task<LayerPublicationPreview> PreviewPublicationAsync(
        string connectionString, LayerPublishRequest request, CancellationToken cancellationToken = default);
}
