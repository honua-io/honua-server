// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> GpServerEndpoints =>
    [
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

        // PrintingTools (Export Web Map Task)
        // Note: task metadata (service info) is served via GET /execute?f=json
        // matching ArcGIS Server behavior. A standalone base URL endpoint cannot be
        // registered due to ASP.NET Core treating decoded %20 as segment separators.
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task"),
        new("POST", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/execute"),
        new("POST", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/submitJob"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/submitJob"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/jobs/{jobId}"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Export Web Map Task/jobs/{jobId}/results/Output_File"),
        new("GET", "/rest/services/Utilities/PrintingTools/GPServer/Get Layout Templates Info Task/execute"),
    ];
}
