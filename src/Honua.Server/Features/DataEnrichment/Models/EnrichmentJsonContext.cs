// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.DataEnrichment.Models;

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
