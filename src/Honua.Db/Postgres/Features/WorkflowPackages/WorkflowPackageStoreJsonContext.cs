// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Db.Postgres.Features.WorkflowPackages;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowGraph))]
[JsonSerializable(typeof(WorkflowNode))]
[JsonSerializable(typeof(WorkflowEdge))]
[JsonSerializable(typeof(WorkflowSchedule))]
[JsonSerializable(typeof(WorkflowPackageValidationResult))]
[JsonSerializable(typeof(WorkflowPackageValidationFailure))]
[JsonSerializable(typeof(Dictionary<string, string>))]
internal sealed partial class WorkflowPackageStoreJsonContext : JsonSerializerContext
{
}
