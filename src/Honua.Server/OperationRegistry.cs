// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

/// <summary>
/// Registry of public-interface operations that are not fully represented by HTTP route metadata alone.
/// Complements <see cref="EndpointRegistry"/> by tracking WFS dispatcher operations and gRPC service methods.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EndpointRegistry"/> enforces HTTP route drift and integration-test coverage.
/// This registry extends that policy to logical operations dispatched within a single route
/// (WFS 2.0) and non-HTTP protocol surfaces (gRPC).
/// </para>
/// <para>
/// Unimplemented operations (e.g. WFS Transaction) are intentionally excluded.
/// Add them here only when the implementation ships.
/// </para>
/// </remarks>
public static class OperationRegistry
{
    // Protocol constants duplicated from Honua.TestKit.Constants.Protocols
    // because Honua.Server does not reference Honua.TestKit.
    // The architecture test AllInterfaceOperationAttributes_ShouldUseRegisteredValues
    // validates that attribute values match entries here, catching any drift.
    private const string Wfs20 = "WFS-2.0";
    private const string Grpc = "Grpc";

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

        // gRPC FeatureService methods (geospatial.v1.FeatureService)
        new(Grpc, "geospatial.v1.FeatureService/QueryFeatures"),
        new(Grpc, "geospatial.v1.FeatureService/QueryFeaturesStream"),
        new(Grpc, "geospatial.v1.FeatureService/ApplyEdits"),
    ];
}

/// <summary>
/// Describes a public-interface operation by protocol and operation name.
/// </summary>
/// <param name="Protocol">Protocol identifier (e.g. "WFS-2.0", "Grpc").</param>
/// <param name="Operation">Operation name or fully-qualified gRPC method path.</param>
public sealed record OperationDefinition(string Protocol, string Operation);
