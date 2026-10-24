// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Db.Postgres.Features.FeatureStore.Internal;

/// <summary>
/// Cache entry for layer SRID information with expiration tracking.
/// </summary>
internal sealed record LayerSridCacheEntry(int? Srid, DateTimeOffset CreatedAt);
