// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Dashboards;

/// <summary>
/// Source-generated JSON context for the dashboard document contracts. Mirrors
/// <c>ReportDocumentJsonContext</c> (reflection-based serialization is disabled app-wide).
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DashboardDocument))]
[JsonSerializable(typeof(DashboardBinding))]
[JsonSerializable(typeof(DashboardPanel))]
[JsonSerializable(typeof(DashboardValidationResult))]
[JsonSerializable(typeof(DashboardValidationIssue))]
[JsonSerializable(typeof(DashboardValidationIssue[]))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class DashboardDocumentJsonContext : JsonSerializerContext
{
}
