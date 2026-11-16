// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal sealed record OpsIncidentContextResponse
{
    public required OpsIncidentContext Context { get; init; }
    public required DateTimeOffset ReadAt { get; init; }
    public string AffectedScope { get; init; } = "Unknown";
    public string Impact { get; init; } = "Unknown";
    public string CauseConfidence { get; init; } = "Unknown";
    public string SafeNextStep { get; init; } =
        "Review source evidence and freshness before diagnosis or action; this context does not authorize changes.";
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(OpsIncidentContextResponse))]
internal sealed partial class OpsIncidentJsonContext : JsonSerializerContext;
