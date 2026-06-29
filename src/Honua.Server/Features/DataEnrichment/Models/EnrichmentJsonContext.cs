// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.DataEnrichment.Models;

/// <summary>
/// AOT-compatible JSON serialization context for the data-enrichment endpoints (#374).
/// The enriched FeatureCollection response reuses the spatial-analytics feature
/// models, which carry their own source-generated context; this context only owns
/// the catalog listing and request DTO wire shapes.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EnrichmentCatalogResponse))]
[JsonSerializable(typeof(EnrichmentDatasetDescriptor))]
[JsonSerializable(typeof(EnrichmentDatasetDescriptor[]))]
[JsonSerializable(typeof(EnrichmentRequest))]
[JsonSerializable(typeof(EnrichmentAggregate))]
[JsonSerializable(typeof(EnrichmentAggregate[]))]
[JsonSerializable(typeof(EnrichmentDatasetsResponse))]
[JsonSerializable(typeof(EnrichmentDatasetMetadata))]
[JsonSerializable(typeof(EnrichmentDatasetMetadata[]))]
[JsonSerializable(typeof(RegisterEnrichmentDatasetRequest))]
[JsonSerializable(typeof(UpdateEnrichmentDatasetRequest))]
internal sealed partial class EnrichmentJsonContext : JsonSerializerContext;
