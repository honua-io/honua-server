// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Authentication;

// Preserve the existing Redis representation: default property names, numeric
// enums, null values and byte-array encoding.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(OAuthClientRecord))]
internal partial class OAuthClientStoreJsonContext : JsonSerializerContext
{
}
