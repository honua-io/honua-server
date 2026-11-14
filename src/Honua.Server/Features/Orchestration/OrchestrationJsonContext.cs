// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Orchestration.Domain;

namespace Honua.Server.Features.Orchestration;

// PostgreSQL jsonb reorders object keys, so polymorphic discriminators such as raster source
// "$type" can follow other properties when workflow records are read back.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    AllowOutOfOrderMetadataProperties = true)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(WorkflowDefinition))]
[JsonSerializable(typeof(WorkflowRun))]
[JsonSerializable(typeof(WorkflowStepState))]
internal sealed partial class OrchestrationJsonContext : JsonSerializerContext
{
}
