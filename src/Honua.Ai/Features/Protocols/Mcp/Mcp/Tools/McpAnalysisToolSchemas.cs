// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;

namespace Honua.Ai.Protocols.Mcp.Tools;

internal static class McpAnalysisToolSchemas
{
    public static readonly JsonElement BufferFeatures = Load("buffer_features");
    public static readonly JsonElement OverlayFeatures = Load("overlay_features");
    public static readonly JsonElement SummarizeStatistics = Load("summarize_statistics");
    public static readonly JsonElement ReprojectFeatures = Load("reproject_features");
    public static readonly JsonElement JoinFeatures = Load("join_features");
    public static readonly JsonElement ExportDataset = Load("export_dataset");

    private static JsonElement Load(string standardName)
    {
        var resourceName = $"Honua.Ai.Mcp.AnalysisSchemas.{standardName}.schema.json";
        using var stream = typeof(McpAnalysisToolSchemas).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded MCP analysis schema '{resourceName}' is missing.");
        using var document = JsonDocument.Parse(stream);
        return document.RootElement.Clone();
    }
}
