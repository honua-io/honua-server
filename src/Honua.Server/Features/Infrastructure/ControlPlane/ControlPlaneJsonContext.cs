// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.Server.Features.Infrastructure.ControlPlane;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkflowOperationRecord))]
[JsonSerializable(typeof(ExecutionJobRecord))]
[JsonSerializable(typeof(DeployTargetDefinition))]
[JsonSerializable(typeof(ExecutionJobDefinition))]
[JsonSerializable(typeof(ExecutionLogEntry))]
internal sealed partial class ControlPlaneJsonContext : JsonSerializerContext
{
}
