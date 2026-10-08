// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

/// <summary>
/// Declares the supported GPServer and PrintingTools endpoints.
/// </summary>
public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> GpServerEndpoints =>
    [
        // ArcGIS SOAP toolbox discovery over the canonical process catalog.
        new("POST", "/services/{serviceId}/GPServer"),

        // GPServer generic adapter (#723, #1262 sync execute)
        new("GET", "/rest/services/{serviceId}/GPServer"),
        new("POST", "/rest/services/{serviceId}/GPServer"),
        new("GET", "/rest/services/{serviceId}/GPServer/{taskName}"),
        new("POST", "/rest/services/{serviceId}/GPServer/{taskName}"),
        new("POST", "/rest/services/{serviceId}/GPServer/{taskName}/submitJob"),
        new("GET", "/rest/services/{serviceId}/GPServer/{taskName}/submitJob"),
        new("POST", "/rest/services/{serviceId}/GPServer/{taskName}/execute"),
        new("GET", "/rest/services/{serviceId}/GPServer/{taskName}/execute"),
        new("GET", "/rest/services/{serviceId}/GPServer/{taskName}/jobs"),
        new("GET", "/rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}"),
        new("GET", "/rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/results/{paramName}"),
        new("GET", "/rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel"),
        new("POST", "/rest/services/{serviceId}/GPServer/{taskName}/jobs/{jobId}/cancel"),

        // PrintingTools GPServer service, task resources, service node, and SOAP binding.
        // Task names contain spaces; those literals stay on their own routes because
        // ASP.NET Core treats a decoded %20 as a segment separator.
        new("GET", "/rest/services/Utilities/PrintingTools"),
        new("POST", "/rest/services/Utilities/PrintingTools"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer"),
        new("POST", "/rest/services/Utilities/PrintingTools/GPServer"),
        new("POST", "/services/Utilities/PrintingTools/GPServer"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Get Layout Templates Info Task"),
        new("POST", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute"),
        new("POST", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/submitJob"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/submitJob"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/jobs/{jobId}"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/jobs/{jobId}/results/Output_File"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Get Layout Templates Info Task/execute"),
    ];
}
