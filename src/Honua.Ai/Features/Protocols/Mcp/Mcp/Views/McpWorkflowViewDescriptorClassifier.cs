// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Buffers;
using System.Text.Json.Serialization;
using Honua.Ai.Protocols.Mcp.Models;
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
    internal const string WorkspaceCanonicalFamily = "honua.workspace.canonical";
    internal const string LocalProcessFamily = "honua.workspace.local-process";

    /// <summary>Describes a tool and attaches any server-authored classification.</summary>
    public static McpToolDescriptor Describe(IMcpTool tool)
    {
        ArgumentNullException.ThrowIfNull(tool);

        var descriptor = tool.Describe();
        // Classify descriptors from the canonical live catalog using the server's
        // exact-name and family-prefix view rules. Classification does not register
        // a tool or grant permission to invoke it.
        var view = tool is StudioDraftToolBase
            ? McpWorkflowViewCatalog.Setup
            : McpWorkflowViewCatalog.All.Values.FirstOrDefault(candidate => candidate.FindStageIndex(descriptor.Name) >= 0);
        if (view is null || view.FindStageIndex(descriptor.Name) < 0)
        {
            return descriptor;
        }

        var classification = JsonSerializer.SerializeToElement(new McpToolMetadataEnvelope
        {
            Studio = new McpStudioToolClassification
            {
                Family = tool is StudioDraftToolBase ? StudioCompositionFamily
                    : McpWorkflowViewCatalog.Processes.Stages.Single(stage => stage.Id == "local-process").Selects(descriptor.Name)
                        ? LocalProcessFamily : WorkspaceCanonicalFamily,
                View = view.Name,
                Revision = view.Revision,
            },
        }, WorkflowViewJsonContext.Default.McpToolMetadataEnvelope);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            if (descriptor.Meta is { ValueKind: JsonValueKind.Object } existing)
            {
                foreach (var property in existing.EnumerateObject())
                {
                    if (!property.NameEquals(StudioMetadataKey)) { property.WriteTo(writer); }
                }
            }
            writer.WritePropertyName(StudioMetadataKey);
            classification.GetProperty(StudioMetadataKey).WriteTo(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        descriptor.Meta = document.RootElement.Clone();

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
