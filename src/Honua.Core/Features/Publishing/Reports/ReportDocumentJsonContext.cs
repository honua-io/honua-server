// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Publishing.Reports;

/// <summary>
/// Source-generated JSON context for the report document contracts. Mirrors
/// <c>FormPackageJsonContext</c> (reflection-based serialization is disabled app-wide).
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ReportDocument))]
[JsonSerializable(typeof(ReportBinding))]
[JsonSerializable(typeof(ReportPanel))]
[JsonSerializable(typeof(ReportValidationResult))]
[JsonSerializable(typeof(ReportValidationIssue))]
[JsonSerializable(typeof(ReportValidationIssue[]))]
[JsonSerializable(typeof(JsonElement))]
public sealed partial class ReportDocumentJsonContext : JsonSerializerContext
{
}
