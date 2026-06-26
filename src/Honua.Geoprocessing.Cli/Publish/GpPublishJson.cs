// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Geoprocessing.Cli.Publish;

/// <summary>
/// JSON options matching the Console workflow-package endpoints' source-generated context
/// (<c>WorkflowPackagesJsonContext</c>): camelCase property names and string enum values.
/// The devkit tool is reflection-friendly (it is never AOT-published), so a plain
/// <see cref="JsonSerializerOptions"/> is used rather than a source-generated context.
/// </summary>
internal static class GpPublishJson
{
    /// <summary>
    /// Shared, immutable serializer options mirroring the server's wire policy.
    /// </summary>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() }
    };
}
