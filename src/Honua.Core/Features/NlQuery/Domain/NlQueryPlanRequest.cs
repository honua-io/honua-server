// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.NlQuery.Domain;

/// <summary>
/// Request for generating a filter plan from a natural-language query.
/// Carries the NL utterance plus the resource's queryable schema so the model
/// can ground its output against real fields.
/// </summary>
/// <param name="Query">The natural-language query string.</param>
/// <param name="Resource">The Metadata v2 resource providing queryable schema metadata.</param>
/// <param name="CollectionId">Optional collection identifier for telemetry.</param>
public sealed record NlQueryPlanRequest(
    string Query,
    MetadataV2Resource Resource,
    string? CollectionId = null);
