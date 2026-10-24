// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Db.Postgres.Features.Alerts;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.Unspecified)]
[JsonSerializable(typeof(Dictionary<string, string?>), TypeInfoPropertyName = "MetadataDictionary")]
internal sealed partial class AlertMetadataJsonContext : JsonSerializerContext
{
}
