// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Deployment.Domain;

/// <summary>
/// Source-generated JSON serialization context for deployment domain models. Provides
/// AOT- and trim-safe serialization for deployment inspection and audit surfaces.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    Converters = [
        typeof(JsonStringEnumConverter<DeploymentSourceKind>),
        typeof(JsonStringEnumConverter<DeploymentKind>),
        typeof(JsonStringEnumConverter<HostingMode>),
        typeof(JsonStringEnumConverter<DeploymentStatus>),
        typeof(JsonStringEnumConverter<DeploymentPublicationState>),
        typeof(JsonStringEnumConverter<RolloutStrategy>),
        typeof(JsonStringEnumConverter<RolloutState>),
        typeof(JsonStringEnumConverter<RuntimeHealth>)
    ])]
[JsonSerializable(typeof(Deployment))]
[JsonSerializable(typeof(DeploymentSource))]
[JsonSerializable(typeof(DeploymentTarget))]
[JsonSerializable(typeof(RolloutPlan))]
[JsonSerializable(typeof(DeploymentSchedule))]
[JsonSerializable(typeof(RuntimeState))]
[JsonSerializable(typeof(DeploymentTransition))]
[JsonSerializable(typeof(DeploymentProgress))]
[JsonSerializable(typeof(IReadOnlyList<DeploymentTransition>))]
[JsonSerializable(typeof(IReadOnlyList<Deployment>))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class DeploymentJsonContext : JsonSerializerContext
{
}
