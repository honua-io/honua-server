// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Operations.Status;

/// <summary>
/// Source-generated JSON context for the aggregated operate-status response. AOT-safe: no
/// reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OperateStatusResponse))]
internal sealed partial class OperateStatusJsonContext : JsonSerializerContext
{
}
