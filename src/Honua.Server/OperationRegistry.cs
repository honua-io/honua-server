// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

/// <summary>
/// Registry of public-interface operations that are not fully represented by HTTP route metadata alone.
/// Complements <see cref="EndpointRegistry"/> by tracking dispatched operations, query-option operation
/// families, and gRPC service methods.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EndpointRegistry"/> enforces HTTP route drift and integration-test coverage.
/// This registry extends that policy to logical operations dispatched within a single route
/// (WFS 2.0 and WMS 1.3.0), option-driven OData surfaces, and non-HTTP protocol surfaces (gRPC).
/// </para>
/// <para>
/// Add new dispatched operations here when the implementation ships so the
/// architecture tests can enforce integration coverage.
/// </para>
/// </remarks>
public static class OperationRegistry
{
    // Protocol constants duplicated from Honua.TestKit.Constants.Protocols
    // because Honua.Server does not reference Honua.TestKit.
    // The architecture test AllInterfaceOperationAttributes_ShouldUseRegisteredValues
    // validates that attribute values match entries here, catching any drift.
    private const string Wfs20 = "WFS-2.0";
    private const string Wms13 = "WMS-1.3.0";
    private const string Wmts10 = "WMTS-1.0.0";
    private const string ODataV4 = "OData-v4";
    private const string Grpc = "Grpc";
    private const string Mcp = "Mcp";

    /// <summary>
    /// All public-interface operations that require integration test coverage.
    /// </summary>
    public static IReadOnlyList<OperationDefinition> All { get; } =
    [
        // WFS 2.0 operations (dispatched via GET|POST /wfs?REQUEST=...)
        new(Wfs20, "GetCapabilities"),
        new(Wfs20, "DescribeFeatureType"),
        new(Wfs20, "GetFeature"),
        new(Wfs20, "GetPropertyValue"),
        new(Wfs20, "Transaction"),
        new(Wfs20, "ListStoredQueries"),
        new(Wfs20, "DescribeStoredQueries"),

        // WMS 1.3.0 operations (dispatched via GET /ogc/services/{id}/wms?REQUEST=...
        // and the GeoServices compatibility /MapServer/WMS alias)
        new(Wms13, "GetCapabilities"),
        new(Wms13, "GetMap"),
        new(Wms13, "GetFeatureInfo"),

        // WMTS 1.0.0 operations (KVP, RESTful, and GeoServices compatibility aliases)
        new(Wmts10, "GetCapabilities"),
        new(Wmts10, "GetTile"),
        new(Wmts10, "GetFeatureInfo"),

        // OData operation families that share routes with query-option or payload dispatch
        new(ODataV4, "Metadata"),
        new(ODataV4, "Batch"),
        new(ODataV4, "Apply"),
        new(ODataV4, "Search"),
        new(ODataV4, "DeltaTracking"),

        // MCP JSON-RPC methods dispatched through POST /mcp
        new(Mcp, "initialize"),
        new(Mcp, "tools/list"),
        new(Mcp, "tools/call"),
        new(Mcp, "resources/list"),
        new(Mcp, "resources/templates/list"),
        new(Mcp, "resources/read"),

        // gRPC FeatureService methods (geospatial.v1.FeatureService)
        new(Grpc, "geospatial.v1.FeatureService/QueryFeatures"),
        new(Grpc, "geospatial.v1.FeatureService/QueryFeaturesStream"),
        new(Grpc, "geospatial.v1.FeatureService/ApplyEdits"),

        // gRPC ProcessService methods (geospatial.v1.ProcessService)
        new(Grpc, "geospatial.v1.ProcessService/ValidatePlan"),
        new(Grpc, "geospatial.v1.ProcessService/DryRunPlan"),
        new(Grpc, "geospatial.v1.ProcessService/ExecutePlan"),
        new(Grpc, "geospatial.v1.ProcessService/SubmitPlanJob"),
        new(Grpc, "geospatial.v1.ProcessService/GetJob"),
        new(Grpc, "geospatial.v1.ProcessService/GetJobResults"),
        new(Grpc, "geospatial.v1.ProcessService/CancelJob"),

        // gRPC SpecService methods (geospatial.v1.SpecService)
        new(Grpc, "geospatial.v1.SpecService/PlanSpec"),
        new(Grpc, "geospatial.v1.SpecService/ApplySpec"),
        new(Grpc, "geospatial.v1.SpecService/CancelApply"),
    ];
}

/// <summary>
/// Describes a public-interface operation by protocol and operation name.
/// </summary>
/// <param name="Protocol">Protocol identifier (e.g. "WFS-2.0", "Grpc").</param>
/// <param name="Operation">Operation name or fully-qualified gRPC method path.</param>
public sealed record OperationDefinition(string Protocol, string Operation);
