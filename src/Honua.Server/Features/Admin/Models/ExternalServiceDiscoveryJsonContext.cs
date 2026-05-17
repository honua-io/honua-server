// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation metadata for external service discovery.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ExternalServiceDiscoveryRequest))]
[JsonSerializable(typeof(ExternalServiceDiscoveryResponse))]
[JsonSerializable(typeof(ExternalServiceLayerCandidate))]
[JsonSerializable(typeof(ExternalServiceLayerCandidate[]))]
[JsonSerializable(typeof(ExternalServiceExtent))]
[JsonSerializable(typeof(ExternalServiceField))]
[JsonSerializable(typeof(ExternalServiceField[]))]
[JsonSerializable(typeof(ArcGisServiceDocument))]
[JsonSerializable(typeof(ArcGisLayerDocument))]
[JsonSerializable(typeof(ArcGisCountDocument))]
[JsonSerializable(typeof(OgcLandingDocument))]
[JsonSerializable(typeof(OgcCollectionsDocument))]
internal sealed partial class ExternalServiceDiscoveryJsonContext : JsonSerializerContext
{
}
