// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for coordinated-release admin APIs (Demo C).
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(CreateCoordinatedReleaseOperationRequest))]
[JsonSerializable(typeof(CoordinatedReleaseActionRequest))]
[JsonSerializable(typeof(CoordinatedReleaseOperationResponse))]
[JsonSerializable(typeof(CoordinatedReleaseArtifactResponse))]
[JsonSerializable(typeof(CoordinatedReleaseStepResponse))]
internal sealed partial class CoordinatedReleaseJsonContext : JsonSerializerContext
{
}
