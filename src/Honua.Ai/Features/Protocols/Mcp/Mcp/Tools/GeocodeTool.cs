// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Authorization.Domain;
using Honua.Geocoding.Features.Geocoding.Abstractions;
using Honua.Geocoding.Features.Geocoding.Domain;
using Honua.Geoprocessing;
using Honua.Infrastructure.Licensing;
using Honua.Ai.Protocols.Mcp.Location;
using Honua.Ai.Protocols.Mcp.Models;

namespace Honua.Ai.Protocols.Mcp.Tools;

/// <summary>
/// MCP tool that forward-geocodes a freeform address into ranked candidate
/// locations. Thin adapter over the canonical
/// <see cref="IGeocodeCoordinatorService"/> pipeline (the same provider
/// registry, priority order, and failover behavior the GeoServices
/// GeocodeServer surface uses), so the MCP surface never grows its own
/// geocoding logic.
/// </summary>
/// <remarks>
/// Edition gating follows the #1592 model: the tool is always advertised in
/// <c>tools/list</c> (discovery is free), but invoking it requires the
/// existing Pro <c>geocoding.forward</c> entitlement from
/// <see cref="Honua.Core.Features.Licensing.Domain.FeatureCatalog"/>. Denials
/// surface as a <c>failed_precondition</c> tool error carrying the upgrade
/// message rather than an HTTP 402, because MCP tool failures travel inside
/// the JSON-RPC result envelope.
/// </remarks>
internal sealed class GeocodeTool : IMcpTool
{
    public const string ToolName = "honua_geocode_address";

    /// <summary>
    /// Entitlement key gating invocation; must exist in
    /// <see cref="Honua.Core.Features.Licensing.Domain.FeatureCatalog.All"/>.
    /// </summary>
    public const string EntitlementKey = "geocoding.forward";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<GeocodeTool> _logger;

    public GeocodeTool(IGeoprocessingJobService jobService, ILogger<GeocodeTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Geocode address",
        Description = "Forward-geocode a freeform address and return ranked candidate locations with coordinates and match scores. Requires the Pro 'geocoding.forward' entitlement.",
        InputSchema = LocationToolSchemas.GeocodeArgumentSchema,
        OutputSchema = McpToolOutputSchemas.GeocodeOutputSchema,
        // Read-only lookup; open-world because resolution can route to external
        // geocoding providers rather than the server's closed catalog.
        Annotations = McpToolAnnotationSets.ReadOnlyOpenWorld("Geocode address")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GeocodeAddress");
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

        var argument = McpToolHelpers.ParseArguments(arguments, LocationJsonContext.Default.McpGeocodeArgument);
        var request = ToDomainRequest(argument);
        var providerName = string.IsNullOrWhiteSpace(argument.Provider) ? null : argument.Provider.Trim();

        // IGeocodeCoordinatorService is scoped (it composes scoped provider
        // registrations), so it is resolved from the request scope instead of
        // being captured by this singleton tool.
        var coordinator = httpContext.RequestServices.GetRequiredService<IGeocodeCoordinatorService>();
        var result = await coordinator
            .ForwardGeocodeAsync(request, providerName, cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess)
        {
            throw new GeoprocessingStoreUnavailableException(
                $"Geocoding failed: {result.ErrorMessage ?? "no provider produced a result."}");
        }

        var output = new McpGeocodeOutput
        {
            Provider = result.ProviderName,
            Candidates = result.Data.Select(static candidate => new McpGeocodeCandidate
            {
                Address = candidate.Address,
                X = candidate.X,
                Y = candidate.Y,
                Score = candidate.Score,
                Srid = candidate.SpatialReferenceWkid,
                MatchLevel = candidate.MatchLevel,
                AddressType = candidate.AddressType
            }).ToList()
        };

        return McpToolHelpers.SuccessResult(output, LocationJsonContext.Default.McpGeocodeOutput);
    }

    private static ForwardGeocodeRequest ToDomainRequest(McpGeocodeArgument argument)
    {
        if (string.IsNullOrWhiteSpace(argument.Address))
        {
            throw new GeoprocessingValidationException("'address' is required and must be non-empty.");
        }

        var maxResults = argument.MaxResults ?? LocationToolSchemas.DefaultGeocodeResults;
        if (maxResults is < LocationToolSchemas.MinGeocodeResults or > LocationToolSchemas.MaxGeocodeResults)
        {
            throw new GeoprocessingValidationException(
                $"'maxResults' must be between {LocationToolSchemas.MinGeocodeResults} and {LocationToolSchemas.MaxGeocodeResults}.");
        }

        var outSrid = argument.OutSrid ?? 4326;
        if (outSrid <= 0)
        {
            throw new GeoprocessingValidationException("'outSrid' must be a positive SRID/WKID.");
        }

        return new ForwardGeocodeRequest(
            Query: argument.Address.Trim(),
            MaxResults: maxResults,
            SpatialReferenceWkid: outSrid,
            CountryCodes: string.IsNullOrWhiteSpace(argument.CountryCodes) ? null : argument.CountryCodes.Trim());
    }
}
