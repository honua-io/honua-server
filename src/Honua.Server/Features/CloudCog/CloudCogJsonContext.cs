// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.CloudCog.Models;

namespace Honua.Server.Features.CloudCog;

/// <summary>
/// JSON serialization context for Cloud COG models.
/// Enables AOT-compatible JSON serialization for Cloud COG endpoints.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(RegisterCloudCogRequest))]
[JsonSerializable(typeof(CloudCogRegistrationResponse))]
[JsonSerializable(typeof(CloudCogRegistrationResponse[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
internal sealed partial class CloudCogJsonContext : JsonSerializerContext
{
}
