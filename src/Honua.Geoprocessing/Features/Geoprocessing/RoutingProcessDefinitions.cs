// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing;

/// <summary>Durable process projections of the canonical routing provider.</summary>
internal static class RoutingProcessDefinitions
{
    internal const string Route = "routing.route";
    internal const string ServiceArea = "routing.service-area";

    internal static readonly ProcessDefinition[] All =
    [
        Create(Route, "Route", "Solves an ordered-stop route using the configured routing network.",
            "RouteSolveRequest JSON: stops (lon/lat points), inSrid, outSrid, travelMode, and optional canonical barriers."),
        Create(ServiceArea, "Service Area", "Solves travel-time service areas using the configured routing network.",
            "ServiceAreaSolveRequest JSON: facilities (lon/lat points), positive breaks in minutes, travelDirection, inSrid, outSrid, travelMode, and optional canonical barriers."),
    ];

    internal static bool IsRouting(string processId) => processId is Route or ServiceArea;

    private static ProcessDefinition Create(string processId, string title, string description, string requestDescription) => new()
    {
        ProcessId = processId,
        Title = title,
        Description = description,
        Category = "routing",
        Parameters =
        [
            new ProcessParameterSpec
            {
                Name = "request",
                DisplayName = "Routing Request",
                Description = requestDescription,
                ValueType = ProcessParameterValueType.Text,
                Required = true,
            },
        ],
        OutputArtifactKinds = [ArtifactKind.FeatureLayer, ArtifactKind.Scalar],
    };
}
