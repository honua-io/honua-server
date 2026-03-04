// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Geocoding;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(GeocodeServerInfoResponse))]
[JsonSerializable(typeof(FindAddressCandidatesResponse))]
[JsonSerializable(typeof(ReverseGeocodeResponse))]
[JsonSerializable(typeof(SuggestResponse))]
[JsonSerializable(typeof(GeocodeCandidateResponse[]))]
[JsonSerializable(typeof(GeocodeSuggestionResponse[]))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class GeocodingJsonContext : JsonSerializerContext
{
}
