// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.EnrichmentCatalog.Domain;

namespace Honua.Core.Features.EnrichmentCatalog.Abstractions;

public interface IEnrichmentDatasetCatalogStore
{
    Task<IReadOnlyList<EnrichmentDatasetRecord>> ListAsync(CancellationToken cancellationToken = default);

    Task<EnrichmentDatasetRecord?> GetAsync(string id, CancellationToken cancellationToken = default);

    Task<EnrichmentDatasetRecord> RegisterAsync(EnrichmentDatasetRecord dataset, CancellationToken cancellationToken = default);

    Task<EnrichmentDatasetRecord?> UpdateAsync(EnrichmentDatasetRecord dataset, CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
