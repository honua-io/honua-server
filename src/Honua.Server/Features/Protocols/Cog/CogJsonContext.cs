// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Protocols.Cog.Models;

namespace Honua.Server.Features.Protocols.Cog;

/// <summary>
/// JSON serialization context for COG models.
/// Enables AOT-compatible JSON serialization for COG endpoints.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RegisterCogRequest))]
[JsonSerializable(typeof(CogRegistrationResponse))]
[JsonSerializable(typeof(CogRegistrationResponse[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
internal sealed partial class CogJsonContext : JsonSerializerContext
{
}
