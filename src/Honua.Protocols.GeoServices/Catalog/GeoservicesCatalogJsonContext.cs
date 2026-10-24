// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.GeoServices.Catalog;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(ServicesDirectoryResponse))]
[JsonSerializable(typeof(ServiceDirectoryEntry[]))]
[JsonSerializable(typeof(RestInfoResponse))]
[JsonSerializable(typeof(RestAuthInfo))]
internal sealed partial class GeoservicesCatalogJsonContext : JsonSerializerContext
{
}
