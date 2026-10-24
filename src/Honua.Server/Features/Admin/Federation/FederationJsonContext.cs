// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Admin.Federation.Models;

namespace Honua.Server.Features.Admin.Federation;

/// <summary>
/// AOT-safe JSON serialization context for the federation admin surface (issue #341).
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(FederationSourceResponse))]
[JsonSerializable(typeof(FederationSourceResponse[]))]
[JsonSerializable(typeof(FederationQueryPlanResponse))]
[JsonSerializable(typeof(string))]
internal sealed partial class FederationJsonContext : JsonSerializerContext
{
}
