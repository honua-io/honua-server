// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.WorkflowPackages.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;

namespace Honua.Server.Features.WorkflowPackages;

/// <summary>
/// Grounds the AI workflow author's <em>authoring/publishing</em> palette: publish, style,
/// map, dashboard, and STAC node types. The built-in geoprocessing process catalog
/// (<see cref="ProcessCatalogWorkflowNodeProvider"/>) only surfaces analysis/transform
/// processes, so prompts such as "publish maui-parcels as a FeatureServer + OGC Features"
/// or "build a web map of parcels/zoning/flood with a legend" previously found no matching
/// node type and the model correctly refused (honua-server#1759). This provider exposes those
/// cartography/publishing capabilities as first-class registry nodes so the grounded palette,
/// the constrained generation schema, and the structural validator all accept them.
/// </summary>
/// <remarks>
/// These nodes are marked <see cref="WorkflowNodeCapabilityFlags.Executable"/> so the AI
/// generation hard gate (<see cref="WorkflowPackageGraphValidator"/>) accepts proposals that
/// use them. They compile to authoring operations, not to the geoprocessing job substrate;
/// the package-to-execution-plan compiler currently understands only <c>process:</c> nodes,
/// so a saved authoring graph still requires the authoring compile path to land separately.
/// The grounding/generation surface — the subject of #1759 — is complete here.
/// </remarks>
internal sealed class AuthoringWorkflowNodeProvider : IWorkflowNodeProvider
{
    /// <summary>Stable provider identifier.</summary>
    public const string Provider = "authoring.publishing-catalog";

    /// <summary>Node-type id prefix for every authoring node (<c>authoring:publish.feature-service</c>).</summary>
    public const string NodeTypePrefix = "authoring:";

    /// <summary>Provider version; bump when the authoring node surface changes.</summary>
    public const string CatalogVersion = "honua.authoring_catalog.builtin.v1";

    /// <inheritdoc />
    public string ProviderId => Provider;

    /// <inheritdoc />
    public string Version => CatalogVersion;

    /// <inheritdoc />
    public Task<IReadOnlyList<WorkflowNodeDefinition>> ListNodesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<WorkflowNodeDefinition>>(Nodes);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetWarningsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
    }

    /// <summary>Builds the prefixed authoring node-type id for the supplied capability key.</summary>
    public static string ToNodeTypeId(string capabilityKey) => NodeTypePrefix + capabilityKey;

    /// <summary>
    /// Extracts the authoring capability key from a node-type id, returning <c>false</c> for
    /// ids that are not owned by this provider.
    /// </summary>
    public static bool TryGetCapabilityKey(string nodeTypeId, out string capabilityKey)
    {
        if (nodeTypeId.StartsWith(NodeTypePrefix, StringComparison.Ordinal))
        {
            capabilityKey = nodeTypeId[NodeTypePrefix.Length..];
            return !string.IsNullOrWhiteSpace(capabilityKey);
        }

        capabilityKey = string.Empty;
        return false;
    }

    private static readonly IReadOnlyList<WorkflowNodeDefinition> Nodes =
    [
        // ---- Publish --------------------------------------------------------------------
        Define(
            capabilityKey: "publish.feature-service",
            category: "publish",
            title: "Publish feature service",
            description: "Publishes a layer or feature collection as a Honua service exposing FeatureServer and OGC API Features endpoints.",
            inputFormat: "feature-layer",
            output: ("publishedService", "published-service"),
            parameters:
            [
                Required("sourceLayerId", "Source layer", "Layer or feature collection to publish.", "layer-id"),
                Required("serviceName", "Service name", "Name of the published service."),
                Optional("protocols", "Protocols", "Comma-separated protocol surfaces to enable, e.g. 'feature-server,ogc-features'.", "featureserver,ogc-features")
            ]),
        Define(
            capabilityKey: "publish.multiprotocol",
            category: "publish",
            title: "Publish multi-protocol service",
            description: "Publishes a layer across multiple protocol adapters (FeatureServer, OGC API Features, WFS, OData) from one shared definition.",
            inputFormat: "feature-layer",
            output: ("publishedService", "published-service"),
            parameters:
            [
                Required("sourceLayerId", "Source layer", "Layer to publish.", "layer-id"),
                Required("serviceName", "Service name", "Name of the published service."),
                Required("protocols", "Protocols", "Comma-separated protocol surfaces to enable.")
            ]),

        // ---- Style ----------------------------------------------------------------------
        Define(
            capabilityKey: "style.choropleth",
            category: "style",
            title: "Style choropleth",
            description: "Authors a graduated/choropleth renderer for a layer keyed by a numeric attribute and a color ramp.",
            inputFormat: "feature-layer",
            output: ("style", "style"),
            parameters:
            [
                Required("layerId", "Layer", "Layer to style.", "layer-id"),
                Required("attribute", "Attribute", "Numeric attribute that drives the class breaks."),
                Optional("classes", "Class count", "Number of class breaks.", "5"),
                Optional("colorRamp", "Color ramp", "Named color ramp to apply.", "Blues")
            ]),
        Define(
            capabilityKey: "style.categorical",
            category: "style",
            title: "Style categorical",
            description: "Authors a unique-value/categorical renderer for a layer keyed by a categorical attribute.",
            inputFormat: "feature-layer",
            output: ("style", "style"),
            parameters:
            [
                Required("layerId", "Layer", "Layer to style.", "layer-id"),
                Required("attribute", "Attribute", "Categorical attribute that drives the unique values.")
            ]),

        // ---- Map ------------------------------------------------------------------------
        Define(
            capabilityKey: "map.web-map",
            category: "map",
            title: "Build web map",
            description: "Composes a web map from one or more styled layers with an optional legend and initial extent.",
            inputFormat: "style",
            output: ("webMap", "web-map"),
            parameters:
            [
                Required("title", "Map title", "Title of the web map."),
                Required("layers", "Layers", "Comma-separated layer ids to include in the map."),
                Optional("includeLegend", "Include legend", "Whether to render a legend.", "true"),
                Optional("basemap", "Basemap", "Named basemap to use.")
            ]),

        // ---- Dashboard ------------------------------------------------------------------
        Define(
            capabilityKey: "dashboard.build",
            category: "dashboard",
            title: "Build dashboard",
            description: "Composes a dashboard of map, chart, and indicator widgets bound to published layers or query results.",
            inputFormat: "web-map",
            output: ("dashboard", "dashboard"),
            parameters:
            [
                Required("title", "Dashboard title", "Title of the dashboard."),
                Required("widgets", "Widgets", "Comma-separated widget kinds, e.g. 'map,chart,indicator'.")
            ]),

        // ---- STAC -----------------------------------------------------------------------
        Define(
            capabilityKey: "stac.catalog",
            category: "stac",
            title: "Create STAC catalog",
            description: "Creates a STAC catalog/collection over imagery or raster assets and exposes the STAC API endpoints.",
            inputFormat: "raster",
            output: ("stacCatalog", "stac-catalog"),
            parameters:
            [
                Required("catalogId", "Catalog id", "Identifier for the STAC catalog."),
                Required("assetSource", "Asset source", "Imagery/raster source to index into the catalog."),
                Optional("collectionId", "Collection id", "Identifier for the STAC collection.")
            ]),
    ];

    private static WorkflowNodeDefinition Define(
        string capabilityKey,
        string category,
        string title,
        string description,
        string inputFormat,
        (string Name, string Format) output,
        IReadOnlyList<WorkflowNodeParameterSchema> parameters)
    {
        var inputs = parameters
            .Where(parameter => parameter.Required)
            .Select(parameter => new WorkflowNodePortSchema
            {
                Name = parameter.Name,
                Title = parameter.DisplayName,
                Required = true,
                Schema = parameter.Schema
            })
            .ToArray();

        return new WorkflowNodeDefinition
        {
            NodeTypeId = ToNodeTypeId(capabilityKey),
            ProviderId = Provider,
            RuntimeKind = WorkflowNodeRuntimeKind.Authoring,
            Title = title,
            Description = description,
            Category = category,
            ParameterSchemas = parameters,
            InputSchemas = inputs,
            OutputSchemas =
            [
                new WorkflowNodePortSchema
                {
                    Name = output.Name,
                    Title = output.Name,
                    Required = false,
                    Schema = new WorkflowSchemaDefinition
                    {
                        Type = WorkflowSchemaValueType.Structured,
                        Format = output.Format
                    }
                }
            ],
            CapabilityFlags = new WorkflowNodeCapabilityFlags
            {
                CanValidate = true,
                CanDryRun = false,
                SupportsJob = true,
                SupportsSchedule = true,
                SupportsProcessEndpoint = false,
                Executable = true
            },
            RuntimeHints = new WorkflowNodeRuntimeHints
            {
                WorkerProfile = "authoring",
                EstimatedDurationSeconds = Math.Max(1, parameters.Count),
                CostWeight = 1.0,
                CostUnit = "relative-cpu"
            }
        };
    }

    private static WorkflowNodeParameterSchema Required(
        string name,
        string displayName,
        string description,
        string? format = null) =>
        new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Required = true,
            Schema = SchemaFor(format)
        };

    private static WorkflowNodeParameterSchema Optional(
        string name,
        string displayName,
        string description,
        string? defaultValue = null) =>
        new()
        {
            Name = name,
            DisplayName = displayName,
            Description = description,
            Required = false,
            DefaultValue = defaultValue,
            Schema = SchemaFor(format: null)
        };

    private static WorkflowSchemaDefinition SchemaFor(string? format) =>
        string.Equals(format, "layer-id", StringComparison.Ordinal)
            ? new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.WholeNumber, Format = "layer-id" }
            : new WorkflowSchemaDefinition { Type = WorkflowSchemaValueType.Text };
}
