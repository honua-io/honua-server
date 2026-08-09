// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Postgres.Features.FeatureStore.Internal;

/// <summary>
/// Cache entry for layer SRID information with expiration tracking.
/// </summary>
internal sealed record LayerSridCacheEntry(int? Srid, DateTimeOffset CreatedAt);
