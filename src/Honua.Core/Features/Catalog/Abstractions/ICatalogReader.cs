// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using System.Text.Json.Serialization;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.Catalog.Abstractions;

public interface ICatalogReader
{
    Task<IReadOnlyList<CatalogSummary>> ListAsync(CatalogReadRequest request, CancellationToken cancellationToken = default);
    Task<CatalogReadResult?> ReadAsync(CatalogReadRequest request, string catalogId, CancellationToken cancellationToken = default);
}

public interface ICatalogProtocolProvider
{
    Task<IReadOnlyList<CatalogProtocolDescriptor>> GetProtocolsAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

public sealed record CatalogReadRequest(ClaimsPrincipal Principal, string BaseUrl);
public sealed record CatalogReadResult(CatalogView Catalog, IReadOnlyList<CatalogItem> Items)
{
    [JsonIgnore] public required MetadataV2GraphSnapshot Snapshot { get; init; }
}
