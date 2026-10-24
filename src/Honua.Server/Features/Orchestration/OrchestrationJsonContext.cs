// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Server.Features.Orchestration;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(WorkflowDefinition))]
[JsonSerializable(typeof(WorkflowRun))]
[JsonSerializable(typeof(WorkflowStepState))]
internal sealed partial class OrchestrationJsonContext : JsonSerializerContext
{
}
