// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Catalog.Domain;

namespace Honua.Core.Features.NlQuery.Domain;

/// <summary>
/// Request for generating a filter plan from a natural-language query.
/// Carries the NL utterance plus the layer's queryable schema so the model
/// can ground its output against real fields.
/// </summary>
/// <param name="Query">The natural-language query string.</param>
/// <param name="Layer">The layer definition providing queryable schema metadata.</param>
/// <param name="CollectionId">Optional collection identifier for telemetry.</param>
public sealed record NlQueryPlanRequest(
    string Query,
    LayerDefinition Layer,
    string? CollectionId = null);
