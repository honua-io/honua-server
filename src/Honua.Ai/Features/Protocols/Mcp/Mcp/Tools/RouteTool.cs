// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Licensing;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Honua.Ai.Protocols.Mcp.Location;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that solves a multi-stop route between ordered stops. Thin
/// adapter over the canonical <see cref="IRoutingProvider"/> pipeline (the
/// same engine the GeoServices NAServer adapter uses), so the MCP surface
/// never grows its own routing logic.
/// </summary>
/// <remarks>
/// Edition gating follows the #1592 model: the tool is always advertised in
/// <c>tools/list</c> (discovery is free), but invoking it requires the Pro
/// <c>routing.solve</c> entitlement from
/// <see cref="Honua.Core.Features.Licensing.Domain.FeatureCatalog"/>. Denials
/// surface as a <c>failed_precondition</c> tool error carrying the upgrade
/// message. When the configured routing provider advertises no route
/// capability (e.g. a non-Postgres data provider without pgRouting), the tool
/// fails with the same <c>failed_precondition</c> channel instead of running
/// SQL that cannot succeed.
/// </remarks>
internal sealed class RouteTool : IMcpTool
{
    public const string ToolName = "honua_solve_route";

    /// <summary>
    /// Entitlement key gating invocation; must exist in
    /// <see cref="Honua.Core.Features.Licensing.Domain.FeatureCatalog.All"/>.
    /// </summary>
    public const string EntitlementKey = "routing.solve";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<RouteTool> _logger;

    public RouteTool(IGeoprocessingJobService jobService, ILogger<RouteTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Solve route",
        Description = "Solve a multi-stop route through ordered stops and return the route geometry, length, travel time, and turn-by-turn directions. Requires the Pro 'routing.solve' entitlement.",
        InputSchema = LocationToolSchemas.RouteArgumentSchema,
        OutputSchema = McpToolOutputSchemas.RouteOutputSchema,
        // Read-only computation; open-world because solving can route to external
        // routing providers rather than the server's closed catalog.
        Annotations = McpToolAnnotationSets.ReadOnlyOpenWorld("Solve route")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("SolveRoute");
        McpLog.ToolInvoked(_logger, ToolName, WorkflowFamily);

        var principal = McpAuthorizationHelper.EnsurePrincipal(httpContext);
        await _jobService
            .EnsureCallerAuthorizedAsync(principal, OperatorResourceType.Process, OperatorOperation.Execute, cancellationToken)
            .ConfigureAwait(false);

        var entitlement = LicenseGate.CheckEntitlement(httpContext.RequestServices, EntitlementKey);
        if (!entitlement.IsActive)
        {
            throw new GeoprocessingPreconditionFailedException(entitlement.UpgradeMessage);
        }

        var argument = McpToolHelpers.ParseArguments(arguments, LocationJsonContext.Default.McpRouteArgument);
        var request = ToDomainRequest(argument);

        // IRoutingProvider is scoped (it composes scoped database connection
        // providers), so it is resolved from the request scope instead of being
        // captured by this singleton tool.
        var provider = httpContext.RequestServices.GetRequiredService<IRoutingProvider>();
        if (!provider.Capabilities.SupportsRoute)
        {
            throw new GeoprocessingPreconditionFailedException(
                $"Route solving is not supported by the configured routing provider '{provider.Name}'. " +
                "Configure a route-capable provider (Routing:Provider) to enable this tool.");
        }

        var result = await provider.SolveRouteAsync(request, cancellationToken).ConfigureAwait(false);

        var output = new McpRouteOutput
        {
            Provider = provider.Name,
            Solved = result.Solved,
            RouteGeometryGeoJson = result.Solved ? result.RouteGeometryGeoJson : null,
            TotalLengthMeters = result.TotalLengthMeters,
            TotalTimeMinutes = result.TotalTimeMinutes,
            Directions = result.Directions.Select(static step => new McpRouteDirectionStep
            {
                Text = step.Text,
                LengthMeters = step.Length,
                TimeMinutes = step.Time,
                ManeuverType = step.ManeuverType
            }).ToList()
        };

        return McpToolHelpers.SuccessResult(output, LocationJsonContext.Default.McpRouteOutput);
    }

    private static RouteSolveRequest ToDomainRequest(McpRouteArgument argument)
    {
        if (argument.Stops is null || argument.Stops.Count < LocationToolSchemas.MinRouteStops)
        {
            throw new GeoprocessingValidationException(
                $"'stops' is required and must contain at least {LocationToolSchemas.MinRouteStops} stops.");
        }

        if (argument.Stops.Count > LocationToolSchemas.MaxRouteStops)
        {
            throw new GeoprocessingValidationException(
                $"'stops' must contain at most {LocationToolSchemas.MaxRouteStops} stops.");
        }

        var stops = new List<RoutePoint>(argument.Stops.Count);
        for (var i = 0; i < argument.Stops.Count; i++)
        {
            var stop = argument.Stops[i];
            if (stop?.Lon is null || stop.Lat is null)
            {
                throw new GeoprocessingValidationException(
                    $"Stop at index {i} is invalid; each stop must be an object with numeric 'lon' and 'lat'.");
            }

            if (!double.IsFinite(stop.Lon.Value) || !double.IsFinite(stop.Lat.Value))
            {
                throw new GeoprocessingValidationException(
                    $"Stop at index {i} has non-finite ordinates.");
            }

            stops.Add(new RoutePoint(stop.Lon.Value, stop.Lat.Value));
        }

        var outSrid = argument.OutSrid ?? 4326;
        if (outSrid <= 0)
        {
            throw new GeoprocessingValidationException("'outSrid' must be a positive SRID/WKID.");
        }

        if (argument.InSrid is <= 0)
        {
            throw new GeoprocessingValidationException("'inSrid' must be a positive SRID/WKID.");
        }

        var travelProfile = string.IsNullOrWhiteSpace(argument.TravelProfile)
            ? "driving"
            : argument.TravelProfile.Trim();

        var request = new RouteSolveRequest(stops, travelProfile, outSrid);
        return argument.InSrid is { } inSrid ? request with { InSrid = inSrid } : request;
    }
}
