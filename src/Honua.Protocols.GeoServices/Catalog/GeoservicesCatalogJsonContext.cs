// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
