// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Authentication;

// Preserve the existing Redis representation: default property names, numeric
// enums, null values and byte-array encoding.
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(OAuthClientRecord))]
internal partial class OAuthClientStoreJsonContext : JsonSerializerContext
{
}
