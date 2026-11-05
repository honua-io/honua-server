// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Capabilities;

namespace Honua.Ai.Protocols.Mcp.Discovery;

internal sealed class CapabilityManifestImplementation
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("isReferenceImplementation")]
    public bool IsReferenceImplementation { get; set; }
}

internal sealed class CapabilityManifestTool
{
    [JsonPropertyName("standardName")]
    public string StandardName { get; set; } = string.Empty;

    [JsonPropertyName("advertisedName")]
    public string AdvertisedName { get; set; } = string.Empty;

    [JsonPropertyName("workflowFamily")]
    public string WorkflowFamily { get; set; } = string.Empty;
}

internal sealed class CapabilityManifestResource
{
    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    [JsonPropertyName("uriForm")]
    public string UriForm { get; set; } = string.Empty;
}

internal sealed class CapabilityManifest
{
    [JsonPropertyName("specDate")]
    public string SpecDate { get; set; } = string.Empty;

    [JsonPropertyName("implementation")]
    public CapabilityManifestImplementation Implementation { get; set; } = new();

    [JsonPropertyName("tools")]
    public IReadOnlyList<CapabilityManifestTool> Tools { get; set; } = [];

    [JsonPropertyName("resources")]
    public IReadOnlyList<CapabilityManifestResource> Resources { get; set; } = [];
}

[JsonSerializable(typeof(CapabilityManifest))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CapabilityManifestJsonContext : JsonSerializerContext;

internal static class CapabilityManifestEmitter
{
    public const string SpecDate = "2026-04-19";

    public const string ImplementationName = "Honua /mcp";

    private static readonly ICapabilityRegistry Registry = new CapabilityRegistry();

    public static IReadOnlyList<CapabilityManifestTool> Tools { get; } = BuildTools(Registry);

    public static IReadOnlyDictionary<string, CapabilityManifestResource> ResourcesByLiveFamily { get; } =
        new Dictionary<string, CapabilityManifestResource>(StringComparer.Ordinal)
        {
            [McpTelemetry.ResourceFamily.Workspaces] =
                Resource("Workspace", "honua://workspaces/{workspace_id}"),
            [McpTelemetry.ResourceFamily.MapPackages] =
                Resource("Map package", "honua://map-packages/{map_package_id}"),
            [McpTelemetry.ResourceFamily.AppPackages] =
                Resource("App package", "honua://app-packages/{app_package_id}"),
            [McpTelemetry.ResourceFamily.PublishedServices] =
                Resource("Published service", "honua://published-services/{published_service_id}"),
            [McpTelemetry.ResourceFamily.Deployments] =
                Resource("Deployment", "honua://deployments/{deployment_id}"),
            [McpTelemetry.ResourceFamily.OpsHealth] =
                Resource("Ops health", "honua://ops/health"),
            [McpTelemetry.ResourceFamily.OpsFindings] =
                Resource("Ops findings", "honua://ops/findings"),
        };

    public static CapabilityManifest EmitManifest() => new()
    {
        SpecDate = SpecDate,
        Implementation = new CapabilityManifestImplementation
        {
            Name = ImplementationName,
            IsReferenceImplementation = true,
        },
        Tools = Tools
            .OrderBy(t => t.AdvertisedName, StringComparer.Ordinal)
            .ToList(),
        Resources = ResourcesByLiveFamily.Values
            .OrderBy(r => r.Family, StringComparer.Ordinal)
            .ToList(),
    };

    public static string EmitJson()
    {
        var json = JsonSerializer.Serialize(
            EmitManifest(),
            CapabilityManifestJsonContext.Default.CapabilityManifest);
        return json + "\n";
    }

    internal static IReadOnlyList<CapabilityManifestTool> BuildTools(ICapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.All
            .Where(d => d.McpToolName is not null)
            .Where(d => !d.IsDynamic)
            .Select(d => Tool(d.McpToolName!, d.StandardName ?? d.McpToolName!, TitleCase(d.Category)))
            .ToList();
    }

    private static string TitleCase(string value) =>
        string.IsNullOrEmpty(value) ? value : char.ToUpperInvariant(value[0]) + value[1..];

    private static CapabilityManifestTool Tool(string advertisedName, string standardName, string workflowFamily) =>
        new()
        {
            StandardName = standardName,
            AdvertisedName = advertisedName,
            WorkflowFamily = workflowFamily,
        };

    private static CapabilityManifestResource Resource(string family, string uriForm) =>
        new() { Family = family, UriForm = uriForm };
}
