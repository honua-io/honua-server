// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Infrastructure.Middleware;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(EsriError))]
internal sealed partial class LimitsEnforcementJsonContext : JsonSerializerContext
{
}
