// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Db.Postgres.Features.WorkflowPackages;

/// <summary>
/// Source-generated JSON for workflow package rows. Property names and enum
/// spellings are the storage contract; they are not the Console wire format.
/// </summary>
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
