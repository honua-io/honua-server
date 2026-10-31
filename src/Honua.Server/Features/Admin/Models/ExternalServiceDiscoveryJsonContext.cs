// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation metadata for external service discovery.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    // Remote ArcGIS services occasionally emit NaN/Infinity (as named literals or strings) for empty-layer
    // extents; tolerate them rather than failing the whole discovery.
    NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals | JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(ExternalServiceDiscoveryRequest))]
[JsonSerializable(typeof(ExternalServiceDiscoveryResponse))]
[JsonSerializable(typeof(ExternalServiceCredentials))]
[JsonSerializable(typeof(ExternalServiceSummary))]
[JsonSerializable(typeof(ExternalServiceSummary[]))]
[JsonSerializable(typeof(ExternalServiceLayerCandidate))]
[JsonSerializable(typeof(ExternalServiceLayerCandidate[]))]
[JsonSerializable(typeof(ExternalServiceExtent))]
[JsonSerializable(typeof(ExternalServiceField))]
[JsonSerializable(typeof(ExternalServiceField[]))]
[JsonSerializable(typeof(GeoServicesServiceDocument))]
[JsonSerializable(typeof(GeoServicesCatalogDocument))]
[JsonSerializable(typeof(GeoServicesTokenDocument))]
[JsonSerializable(typeof(OAuthTokenDocument))]
[JsonSerializable(typeof(GeoServicesLayerDocument))]
[JsonSerializable(typeof(GeoServicesCountDocument))]
[JsonSerializable(typeof(OgcLandingDocument))]
[JsonSerializable(typeof(OgcCollectionsDocument))]
internal sealed partial class ExternalServiceDiscoveryJsonContext : JsonSerializerContext
{
}
