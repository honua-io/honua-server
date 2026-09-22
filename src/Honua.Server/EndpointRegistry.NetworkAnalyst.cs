// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> NetworkAnalystEndpoints =>
    [
        // NAServer minimal mobile routing compatibility (#366)
        // Service root: Esri desktop clients read it to discover the analysis
        // layers before any solve; without it they refuse the service outright.
        new("GET", "/rest/services/{serviceId}/NAServer"),
        new("GET", "/rest/services/{serviceId}/NAServer/{layerName}"),
        new("GET", "/rest/services/{serviceId}/NAServer/Route/solve"),
        new("POST", "/rest/services/{serviceId}/NAServer/Route/solve"),
        new("POST", "/rest/services/{serviceId}/NAServer/ServiceArea/solveServiceArea"),
        new("POST", "/rest/services/{serviceId}/NAServer/ClosestFacility/solveClosestFacility"),
        new("POST", "/rest/services/{serviceId}/NAServer/ODCostMatrix/solveODCostMatrix"),
        new("POST", "/rest/services/{serviceId}/NAServer/LocationAllocation/solveLocationAllocation"),
    ];
}
