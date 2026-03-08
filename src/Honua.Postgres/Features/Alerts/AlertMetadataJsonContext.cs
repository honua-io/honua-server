// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Postgres.Features.Alerts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(Dictionary<string, string?>), TypeInfoPropertyName = "MetadataDictionary")]
internal sealed partial class AlertMetadataJsonContext : JsonSerializerContext
{
}
