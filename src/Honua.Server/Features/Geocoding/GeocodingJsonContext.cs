// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Geocoding;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(GeocodeServerInfoResponse))]
[JsonSerializable(typeof(FindAddressCandidatesResponse))]
[JsonSerializable(typeof(ReverseGeocodeResponse))]
[JsonSerializable(typeof(SuggestResponse))]
[JsonSerializable(typeof(GeocodeAddressesResponse))]
[JsonSerializable(typeof(GeocodeCandidateResponse[]))]
[JsonSerializable(typeof(GeocodeSuggestionResponse[]))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class GeocodingJsonContext : JsonSerializerContext
{
}
