// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.FieldWorkflows.Export;

/// <summary>
/// Source-generated JSON context for back-office field export contracts.
/// </summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.General,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FieldExportRequest))]
[JsonSerializable(typeof(FieldExportRecord))]
[JsonSerializable(typeof(FieldExportRecord[]))]
[JsonSerializable(typeof(FieldExportRecordListResult))]
public sealed partial class FieldExportJsonContext : JsonSerializerContext
{
}
