// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Capabilities;

namespace Honua.Ai.Protocols.Mcp.Discovery;

/// <summary>
/// Identity block of an emitted geospatial-mcp conformance manifest.
/// </summary>
internal sealed class CapabilityManifestImplementation
{
    /// <summary>Human-readable implementation name (e.g. <c>Honua /mcp</c>).</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// True only for the reference implementation named in the geospatial-mcp
    /// <c>CONFORMANCE.md</c> (Honua). Always <c>true</c> for this emitter.
    /// </summary>
    [JsonPropertyName("isReferenceImplementation")]
    public bool IsReferenceImplementation { get; set; }
}

/// <summary>
/// One advertised tool entry in an emitted geospatial-mcp conformance manifest.
/// Shape mirrors <c>spec/schemas/conformance/manifest.schema.json</c> — the
/// <c>tools[]</c> item is <c>{standardName, advertisedName, workflowFamily}</c>.
/// </summary>
internal sealed class CapabilityManifestTool
{
    /// <summary>The bare geospatial-mcp taxonomy tool name this tool implements.</summary>
    [JsonPropertyName("standardName")]
    public string StandardName { get; set; } = string.Empty;

    /// <summary>The name the live <c>/mcp</c> surface advertises on <c>tools/list</c>.</summary>
    [JsonPropertyName("advertisedName")]
    public string AdvertisedName { get; set; } = string.Empty;

    /// <summary>The workflow-family tag the live surface attaches to the tool.</summary>
    [JsonPropertyName("workflowFamily")]
    public string WorkflowFamily { get; set; } = string.Empty;
}

/// <summary>
/// One advertised resource-family entry in an emitted geospatial-mcp conformance
/// manifest. Shape mirrors the manifest schema's <c>resources[]</c> item
/// <c>{family, uriForm}</c>.
/// </summary>
internal sealed class CapabilityManifestResource
{
    /// <summary>The geospatial-mcp standard resource-family label.</summary>
    [JsonPropertyName("family")]
    public string Family { get; set; } = string.Empty;

    /// <summary>The <c>honua://</c> URI template the family is served under (Decision A grammar).</summary>
    [JsonPropertyName("uriForm")]
    public string UriForm { get; set; } = string.Empty;
}

/// <summary>
/// The full emitted geospatial-mcp conformance manifest document.
/// </summary>
internal sealed class CapabilityManifest
{
    /// <summary>
    /// The <c>Date</c> header of the <c>spec/conformance.md</c> revision this
    /// manifest is produced against.
    /// </summary>
    [JsonPropertyName("specDate")]
    public string SpecDate { get; set; } = string.Empty;

    /// <summary>Identity of the implementation under test.</summary>
    [JsonPropertyName("implementation")]
    public CapabilityManifestImplementation Implementation { get; set; } = new();

    /// <summary>Advertised tools that map onto standard vocabulary.</summary>
    [JsonPropertyName("tools")]
    public IReadOnlyList<CapabilityManifestTool> Tools { get; set; } = [];

    /// <summary>Advertised resource families that map onto standard vocabulary.</summary>
    [JsonPropertyName("resources")]
    public IReadOnlyList<CapabilityManifestResource> Resources { get; set; } = [];
}

/// <summary>
/// AOT-compatible source-generated JSON context for the emitted geospatial-mcp
/// conformance manifest. <c>WriteIndented</c> is on so the emitted file is a
/// human-reviewable, diff-stable artifact.
/// </summary>
[JsonSerializable(typeof(CapabilityManifest))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class CapabilityManifestJsonContext : JsonSerializerContext;

/// <summary>
/// Emits the geospatial-mcp <c>honua.manifest.json</c> conformance manifest from
/// the live MCP operator surface (#2323, Track A). It is the single source of
/// truth for the reference manifest the geospatial-mcp repo vendors: the manifest
/// is <b>generated</b> from this projection rather than hand-maintained, so the
/// server grammar (Decision A: <c>honua://map-packages/{id}</c>,
/// <c>app-packages/{id}</c>, <c>published-services/{id}</c>) is always the
/// contract the published spec aligns to.
/// </summary>
/// <remarks>
/// <para>
/// The projection is bound to the live catalog by the MCP taxonomy-alignment
/// conformance tests: they assert every advertised tool in
/// <see cref="Tools"/> is present in the live <c>tools/list</c> roster (and vice
/// versa), that each entry's <see cref="CapabilityManifestTool.WorkflowFamily"/>
/// matches the live handler's telemetry family, and that every emitted resource
/// <see cref="CapabilityManifestResource.UriForm"/> structurally matches the
/// live <c>resources/templates/list</c> URI template for that family. A tool or
/// URI-grammar change that is not reflected here is therefore a build failure.
/// </para>
/// <para>
/// The result families deliberately cover only what the live surface actually
/// serves. The spec's stand-alone <c>honua://results/{result_package_id}</c>
/// package/artifact/provenance sub-resources are NOT served (results are reached
/// through the job at <c>honua://jobs/{jobId}/results</c>), so they are recorded
/// as <c>known-gap</c> in the vendored index rather than invented here.
/// </para>
/// </remarks>
internal static class CapabilityManifestEmitter
{
    /// <summary>
    /// The <c>Date</c> header of the <c>spec/conformance.md</c> revision the
    /// emitted manifest is produced against.
    /// </summary>
    public const string SpecDate = "2026-04-19";

    /// <summary>The implementation name recorded in the emitted manifest.</summary>
    public const string ImplementationName = "Honua /mcp";

    /// <summary>
    /// The unified capability registry (ADR-0058, Decision B) is the single source
    /// of truth for the <c>/mcp</c> tool roster. B2 (#2334) re-points this emitter
    /// to project its advertised tools from the registry rather than a
    /// hand-maintained list, so the emitted manifest cannot drift from the registry
    /// the live surface is bound to.
    /// </summary>
    private static readonly ICapabilityRegistry Registry = new CapabilityRegistry();

    /// <summary>
    /// The advertised tool projection: <c>advertisedName -&gt; (standardName,
    /// workflowFamily)</c>, projected from the capability registry's <c>/mcp</c>
    /// tool descriptors. Title-cased workflow families mirror the live
    /// <see cref="McpTelemetry.WorkflowFamily"/> telemetry tags (the registry stores
    /// the lower-cased family in <see cref="CapabilityDescriptor.Category"/>).
    /// </summary>
    public static IReadOnlyList<CapabilityManifestTool> Tools { get; } = BuildTools(Registry);

    /// <summary>
    /// The served resource-family projection keyed by the live
    /// <c>IMcpResource.Family</c> tag (<see cref="McpTelemetry.ResourceFamily"/>).
    /// Only families the live surface actually serves are present; each carries
    /// the standard family label and the Decision-A snake_case URI form the
    /// vendored index pins.
    /// </summary>
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

    /// <summary>
    /// Builds the emitted manifest document from the canonical projection, with
    /// tools ordered by advertised name and resources by family for a diff-stable
    /// artifact.
    /// </summary>
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

    /// <summary>
    /// Serializes <see cref="EmitManifest"/> to the newline-terminated,
    /// indented JSON the geospatial-mcp repo vendors as
    /// <c>conformance/manifests/honua.manifest.json</c>.
    /// </summary>
    public static string EmitJson()
    {
        var json = JsonSerializer.Serialize(
            EmitManifest(),
            CapabilityManifestJsonContext.Default.CapabilityManifest);
        return json + "\n";
    }

    /// <summary>
    /// Projects the advertised-tool manifest entries from the capability registry's
    /// <c>/mcp</c> tool descriptors (those that carry an
    /// <see cref="CapabilityDescriptor.McpToolName"/>). This is the provenance move
    /// in ADR-0058 B2 (#2334): the tool roster is the registry's, not a local list.
    /// </summary>
    /// <param name="registry">The capability registry to project the roster from.</param>
    internal static IReadOnlyList<CapabilityManifestTool> BuildTools(ICapabilityRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        return registry.All
            .Where(d => d.McpToolName is not null)
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
