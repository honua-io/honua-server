// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
[JsonSerializable(typeof(ArcGisServiceDocument))]
[JsonSerializable(typeof(ArcGisCatalogDocument))]
[JsonSerializable(typeof(ArcGisTokenDocument))]
[JsonSerializable(typeof(OAuthTokenDocument))]
[JsonSerializable(typeof(ArcGisLayerDocument))]
[JsonSerializable(typeof(ArcGisCountDocument))]
[JsonSerializable(typeof(OgcLandingDocument))]
[JsonSerializable(typeof(OgcCollectionsDocument))]
internal sealed partial class ExternalServiceDiscoveryJsonContext : JsonSerializerContext
{
}
