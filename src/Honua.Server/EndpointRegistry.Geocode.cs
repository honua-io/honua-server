// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server;

public static partial class EndpointRegistry
{
    // Expression-bodied (computed) so it is a method, not a static field
    // initializer; this keeps `All` independent of cross-file static-init order.
    private static IReadOnlyList<EndpointDefinition> GeocodeEndpoints =>
    [
        new("GET", "/rest/services"),
        new("GET", "/rest/info"),
        new("GET", "/rest/services/{locatorName}/GeocodeServer"),
        new("POST", "/rest/services/{locatorName}/GeocodeServer"),
        new("GET", "/rest/services/{locatorName}/GeocodeServer/findAddressCandidates"),
        new("POST", "/rest/services/{locatorName}/GeocodeServer/findAddressCandidates"),
        new("GET", "/rest/services/{locatorName}/GeocodeServer/reverseGeocode"),
        new("POST", "/rest/services/{locatorName}/GeocodeServer/reverseGeocode"),
        new("GET", "/rest/services/{locatorName}/GeocodeServer/suggest"),
        new("POST", "/rest/services/{locatorName}/GeocodeServer/suggest"),
        new("GET", "/rest/services/{locatorName}/GeocodeServer/geocodeAddresses"),
        new("POST", "/rest/services/{locatorName}/GeocodeServer/geocodeAddresses"),
        new("GET", "/rest/services/GeocodeServer"),
        new("POST", "/rest/services/GeocodeServer"),
        new("GET", "/rest/services/GeocodeServer/findAddressCandidates"),
        new("GET", "/rest/services/GeocodeServer/reverseGeocode"),
        new("GET", "/rest/services/GeocodeServer/suggest"),
        new("GET", "/rest/services/GeocodeServer/geocodeAddresses"),

    ];
}
