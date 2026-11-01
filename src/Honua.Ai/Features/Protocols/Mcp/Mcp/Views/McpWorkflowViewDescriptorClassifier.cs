// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Studio;
using Honua.Ai.Protocols.Mcp.Tools;

namespace Honua.Ai.Protocols.Mcp.Views;

/// <summary>
/// Adds server-owned workflow classification to canonical live descriptors.
/// The classification is projected at the catalog boundary so full and narrowed
/// discovery surfaces expose the same descriptor and clients never own a second
/// tool-family allowlist.
/// </summary>
internal static class McpWorkflowViewDescriptorClassifier
{
    internal const string StudioMetadataKey = "honua.studio";
    internal const string StudioCompositionFamily = "honua.studio.composition";

    /// <summary>Describes a tool and attaches any server-authored classification.</summary>
    public static McpToolDescriptor Describe(IMcpTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var descriptor = tool.Describe();
        // Composition needs authorized source identities as well as draft mutations.
        // Classify these existing read-only adapters by type, preserving their canonical
        // descriptors and per-call authorization. Other query/admin tools stay separate.
        if (tool is not (StudioDraftToolBase or ListLayersTool or DescribeLayerTool))
        {
            return descriptor;
        }

        var view = McpWorkflowViewCatalog.Setup;
        if (view.FindStageIndex(descriptor.Name) < 0)
        {
            return descriptor;
        }

        descriptor.Meta = JsonSerializer.SerializeToElement(new McpToolMetadataEnvelope
        {
            Studio = new McpStudioToolClassification
            {
                Family = StudioCompositionFamily,
                View = view.Name,
                Revision = view.Revision,
            },
        }, WorkflowViewJsonContext.Default.McpToolMetadataEnvelope);

        return descriptor;
    }
}

internal sealed class McpToolMetadataEnvelope
{
    [JsonPropertyName(McpWorkflowViewDescriptorClassifier.StudioMetadataKey)]
    public McpStudioToolClassification? Studio { get; set; }
}

internal sealed class McpStudioToolClassification
{
    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("view")]
    public string View { get; set; } = string.Empty;

    [JsonPropertyName("revision")]
    public string Revision { get; set; } = string.Empty;
}
