// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Portal.Domain;

namespace Honua.Protocols.GeoServices.Sharing;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(GenerateTokenResponse))]
[JsonSerializable(typeof(SharingInfoResponse))]
[JsonSerializable(typeof(PortalSelfResponse))]
[JsonSerializable(typeof(CommunitySelfResponse))]
[JsonSerializable(typeof(SearchResponse))]
[JsonSerializable(typeof(PortalItem))]
internal sealed partial class SharingRestJsonContext : JsonSerializerContext
{
}
