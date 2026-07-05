// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Infrastructure.Monitoring;

/// <summary>
/// Source-generated JSON context for the admin ops-health snapshot and ops-findings responses.
/// AOT-safe: no reflection-based serialization.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OpsHealthSnapshotResponse))]
[JsonSerializable(typeof(OpsFindingsListResponse))]
[JsonSerializable(typeof(OpsFindingProposeResponse))]
internal sealed partial class OpsObservabilityJsonContext : JsonSerializerContext
{
}
