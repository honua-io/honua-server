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
/// MCP tool that forward-geocodes a batch of freeform addresses in one call,
/// returning one result per input in input order. Thin adapter over the same
/// canonical <see cref="IGeocodeCoordinatorService"/> pipeline as
/// <see cref="GeocodeTool"/>; each address is resolved independently so a
/// failed address surfaces as a per-item error instead of failing the batch
/// (the coordinator's native <c>BatchGeocodeAsync</c> collapses errors to batch
/// granularity, which would lose that per-item contract).
/// </summary>
/// <remarks>
/// Edition gating follows the #1592 model: the tool is always advertised in
/// <c>tools/list</c>, but invoking it requires the Enterprise
/// <c>geocoding.batch</c> entitlement from
/// <see cref="Honua.Core.Features.Licensing.Domain.FeatureCatalog"/> ("geocode
/// multiple addresses in a single request") — deliberately not the Pro
/// <c>geocoding.forward</c> key the single-address tool uses, so the batch
/// capability stays aligned with the licensing catalog.
/// </remarks>
internal sealed class GeocodeAddressesTool : IMcpTool
{
    public const string ToolName = "honua_geocode_addresses";

    /// <summary>
    /// Entitlement key gating invocation; must exist in
    /// <see cref="Honua.Core.Features.Licensing.Domain.FeatureCatalog.All"/>.
    /// </summary>
    public const string EntitlementKey = "geocoding.batch";

    private readonly IGeoprocessingJobService _jobService;
    private readonly ILogger<GeocodeAddressesTool> _logger;

    public GeocodeAddressesTool(IGeoprocessingJobService jobService, ILogger<GeocodeAddressesTool> logger)
    {
        _jobService = jobService;
        _logger = logger;
    }

    public string Name => ToolName;

    public string WorkflowFamily => McpTelemetry.WorkflowFamily.Execution;

    public McpToolDescriptor Describe() => new()
    {
        Name = ToolName,
        Title = "Geocode addresses (batch)",
        Description = "Forward-geocode a batch of up to 100 freeform addresses in one call. Returns one entry per input address, in input order, "
            + "each as {input, ok, location?, score?, matchedAddress?, error?} where location is {x, y, srid} with x=longitude and y=latitude "
            + "in outSrid (default EPSG:4326, lon/lat order). Partial failures do not fail the call: an unmatched address returns ok=false with an "
            + "error while the rest succeed. This is the first step of the CSV-of-addresses workflow: geocode the address column here, add the "
            + "returned lon/lat values to your rows, then load them with honua_ingest_dataset (CSV with longitudeColumn/latitudeColumn, or a "
            + "GeoJSON FeatureCollection) and publish the resulting table with honua_publish_service. Requires the Enterprise 'geocoding.batch' "
            + "entitlement. For a single address with ranked candidates use honua_geocode_address.",
        InputSchema = LocationToolSchemas.GeocodeAddressesArgumentSchema,
        OutputSchema = McpToolOutputSchemas.GeocodeAddressesOutputSchema,
        // Read-only lookup; open-world because resolution can route to external
        // geocoding providers rather than the server's closed catalog.
        Annotations = McpToolAnnotationSets.ReadOnlyOpenWorld("Geocode addresses (batch)")
    };

    public async Task<McpToolsCallResult> InvokeAsync(
        HttpContext httpContext,
        JsonElement? arguments,
        CancellationToken cancellationToken)
    {
        McpTelemetry.EnrichActivity("GeocodeAddresses");
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

        var argument = McpToolHelpers.ParseArguments(arguments, LocationJsonContext.Default.McpGeocodeAddressesArgument);
        var (addresses, outSrid, countryCodes, providerName) = ValidateArguments(argument);

        // IGeocodeCoordinatorService is scoped (it composes scoped provider
        // registrations), so it is resolved from the request scope instead of
        // being captured by this singleton tool.
        var coordinator = httpContext.RequestServices.GetRequiredService<IGeocodeCoordinatorService>();

        var results = new List<McpGeocodeAddressResult>(addresses.Count);
        var succeeded = 0;

        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = await GeocodeOneAsync(coordinator, address, outSrid, countryCodes, providerName, cancellationToken)
                .ConfigureAwait(false);
            if (item.Ok)
            {
                succeeded++;
            }

            results.Add(item);
        }

        var output = new McpGeocodeAddressesOutput
        {
            Results = results,
            Succeeded = succeeded,
            Failed = results.Count - succeeded,
            Srid = outSrid
        };

        return McpToolHelpers.SuccessResult(output, LocationJsonContext.Default.McpGeocodeAddressesOutput);
    }

    private static (IReadOnlyList<string> Addresses, int OutSrid, string? CountryCodes, string? Provider) ValidateArguments(
        McpGeocodeAddressesArgument argument)
    {
        if (argument.Addresses is not { Count: > 0 })
        {
            throw new GeoprocessingValidationException("'addresses' is required and must contain at least one address.");
        }

        if (argument.Addresses.Count > LocationToolSchemas.MaxBatchAddresses)
        {
            throw new GeoprocessingValidationException(
                $"'addresses' accepts at most {LocationToolSchemas.MaxBatchAddresses} addresses per call "
                + $"(received {argument.Addresses.Count}). Split the batch into chunks of {LocationToolSchemas.MaxBatchAddresses} "
                + "and call this tool once per chunk.");
        }

        var addresses = new List<string>(argument.Addresses.Count);
        for (var i = 0; i < argument.Addresses.Count; i++)
        {
            var address = argument.Addresses[i];
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new GeoprocessingValidationException(
                    $"'addresses[{i}]' must be a non-empty address string.");
            }

            addresses.Add(address.Trim());
        }

        var outSrid = argument.OutSrid ?? 4326;
        if (outSrid <= 0)
        {
            throw new GeoprocessingValidationException("'outSrid' must be a positive SRID/WKID.");
        }

        var countryCodes = string.IsNullOrWhiteSpace(argument.CountryCodes) ? null : argument.CountryCodes.Trim();
        var provider = string.IsNullOrWhiteSpace(argument.Provider) ? null : argument.Provider.Trim();
        return (addresses, outSrid, countryCodes, provider);
    }

    private static async Task<McpGeocodeAddressResult> GeocodeOneAsync(
        IGeocodeCoordinatorService coordinator,
        string address,
        int outSrid,
        string? countryCodes,
        string? providerName,
        CancellationToken cancellationToken)
    {
        var item = new McpGeocodeAddressResult { Input = address };
        try
        {
            var request = new ForwardGeocodeRequest(
                Query: address,
                MaxResults: 1,
                SpatialReferenceWkid: outSrid,
                CountryCodes: countryCodes);

            var result = await coordinator
                .ForwardGeocodeAsync(request, providerName, cancellationToken)
                .ConfigureAwait(false);

            if (!result.IsSuccess)
            {
                item.Error = result.ErrorMessage ?? "No geocoding provider produced a result.";
                return item;
            }

            var candidate = result.Data.FirstOrDefault(static c => c.IsMatch && !double.IsNaN(c.X) && !double.IsNaN(c.Y));
            if (candidate is null)
            {
                item.Error = "No match found for the address.";
                return item;
            }

            item.Ok = true;
            item.Location = new McpGeocodeAddressLocation
            {
                X = candidate.X,
                Y = candidate.Y,
                Srid = candidate.SpatialReferenceWkid
            };
            item.Score = candidate.Score;
            item.MatchedAddress = candidate.Address;
            item.MatchLevel = candidate.MatchLevel;
            item.Provider = result.ProviderName;
            return item;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // One provider blow-up must not fail the remaining addresses. The raw
            // exception is not relayed (it can carry provider internals); the
            // coordinator has already logged the failure through its own pipeline.
            item.Error = "Geocoding failed for this address.";
            return item;
        }
    }
}
