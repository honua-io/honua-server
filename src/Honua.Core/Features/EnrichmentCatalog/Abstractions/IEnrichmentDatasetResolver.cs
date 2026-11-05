// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.EnrichmentCatalog.Domain;

namespace Honua.Core.Features.EnrichmentCatalog.Abstractions;

// A deployment without the enrichment slice does not register this, and consumers fail closed.
public interface IEnrichmentDatasetResolver
{
    Task<EnrichmentDatasetDefinition?> ResolveAsync(string idOrKey, CancellationToken cancellationToken);
}
