// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.
//
// Read-only Esri-compatible GeocodeServer surface; anonymous by design. The
// POST variants of every operation mirror the GET form and only carry query
// parameters in the body — they perform no server-side mutation. Each POST
// route opts into AllowAnonymous explicitly so authorization-policy tooling
// can see the intent rather than treating it as an accidental gap.

using Honua.Infrastructure.Helpers;

namespace Honua.Server.Features.Geocoding;

internal static class GeocodingEndpoints
{
    public static IEndpointRouteBuilder MapGeocodingEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/rest/services/{locatorName}/GeocodeServer", HandleMetadata)
            .WithDisplayName("Get GeocodeServer Metadata")
            .WithName("GetGeocodeServerMetadata")
            .WithSummary("Get GeocodeServer metadata")
            .WithDescription("Returns metadata for the configured geocoding service")
            .WithTags("GeocodeServer");

        endpoints.MapPost("/rest/services/{locatorName}/GeocodeServer", HandleMetadata)
            .WithDisplayName("Get GeocodeServer Metadata (POST)")
            .WithName("GetGeocodeServerMetadataPost")
            .WithSummary("Get GeocodeServer metadata using POST")
            .WithDescription("Returns metadata for the configured geocoding service")
            .WithTags("GeocodeServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{locatorName}/GeocodeServer/findAddressCandidates", HandleFindAddressCandidates)
            .WithDisplayName("Find Address Candidates")
            .WithName("FindAddressCandidates")
            .WithSummary("Forward geocode an address")
            .WithDescription("Find address candidates from freeform address text")
            .WithTags("GeocodeServer");

        endpoints.MapPost("/rest/services/{locatorName}/GeocodeServer/findAddressCandidates", HandleFindAddressCandidates)
            .WithDisplayName("Find Address Candidates (POST)")
            .WithName("FindAddressCandidatesPost")
            .WithSummary("Forward geocode an address using POST")
            .WithDescription("Find address candidates from freeform address text")
            .WithTags("GeocodeServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{locatorName}/GeocodeServer/reverseGeocode", HandleReverseGeocode)
            .WithDisplayName("Reverse Geocode")
            .WithName("ReverseGeocode")
            .WithSummary("Reverse geocode coordinates")
            .WithDescription("Resolve coordinates to the nearest known address")
            .WithTags("GeocodeServer");

        endpoints.MapPost("/rest/services/{locatorName}/GeocodeServer/reverseGeocode", HandleReverseGeocode)
            .WithDisplayName("Reverse Geocode (POST)")
            .WithName("ReverseGeocodePost")
            .WithSummary("Reverse geocode coordinates using POST")
            .WithDescription("Resolve coordinates to the nearest known address")
            .WithTags("GeocodeServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{locatorName}/GeocodeServer/suggest", HandleSuggest)
            .WithDisplayName("Suggest Addresses")
            .WithName("SuggestAddresses")
            .WithSummary("Suggest addresses for partial text")
            .WithDescription("Returns ranked geocode suggestions")
            .WithTags("GeocodeServer");

        endpoints.MapPost("/rest/services/{locatorName}/GeocodeServer/suggest", HandleSuggest)
            .WithDisplayName("Suggest Addresses (POST)")
            .WithName("SuggestAddressesPost")
            .WithSummary("Suggest addresses for partial text using POST")
            .WithDescription("Returns ranked geocode suggestions")
            .WithTags("GeocodeServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/{locatorName}/GeocodeServer/geocodeAddresses", HandleBatch)
            .WithDisplayName("Batch Geocode Addresses")
            .WithName("BatchGeocodeAddresses")
            .WithSummary("Batch geocode addresses")
            .WithDescription("Batch geocoding operation with provider capability checks")
            .WithTags("GeocodeServer");

        endpoints.MapPost("/rest/services/{locatorName}/GeocodeServer/geocodeAddresses", HandleBatch)
            .WithDisplayName("Batch Geocode Addresses (POST)")
            .WithName("BatchGeocodeAddressesPost")
            .WithSummary("Batch geocode addresses using POST")
            .WithDescription("Batch geocoding operation with provider capability checks")
            .WithTags("GeocodeServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/GeocodeServer", static (HttpContext context, GeocodingHandler handler) =>
                handler.HandleMetadataAsync(context, locatorName: null, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context)))
            .WithDisplayName("Get GeocodeServer Metadata (Alias)")
            .WithName("GetGeocodeServerMetadataAlias")
            .WithTags("GeocodeServer");

        endpoints.MapPost("/rest/services/GeocodeServer", static (HttpContext context, GeocodingHandler handler) =>
                handler.HandleMetadataAsync(context, locatorName: null, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context)))
            .WithDisplayName("Get GeocodeServer Metadata (Alias, POST)")
            .WithName("GetGeocodeServerMetadataAliasPost")
            .WithTags("GeocodeServer")
            .AllowAnonymous();

        endpoints.MapGet("/rest/services/GeocodeServer/findAddressCandidates", static (HttpContext context, GeocodingHandler handler) =>
                handler.HandleFindAddressCandidatesAsync(context, locatorName: null, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context)))
            .WithDisplayName("Find Address Candidates (Alias)")
            .WithName("FindAddressCandidatesAlias")
            .WithTags("GeocodeServer");

        endpoints.MapGet("/rest/services/GeocodeServer/reverseGeocode", static (HttpContext context, GeocodingHandler handler) =>
                handler.HandleReverseGeocodeAsync(context, locatorName: null, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context)))
            .WithDisplayName("Reverse Geocode (Alias)")
            .WithName("ReverseGeocodeAlias")
            .WithTags("GeocodeServer");

        endpoints.MapGet("/rest/services/GeocodeServer/suggest", static (HttpContext context, GeocodingHandler handler) =>
                handler.HandleSuggestAsync(context, locatorName: null, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context)))
            .WithDisplayName("Suggest Addresses (Alias)")
            .WithName("SuggestAddressesAlias")
            .WithTags("GeocodeServer");

        endpoints.MapGet("/rest/services/GeocodeServer/geocodeAddresses", static (HttpContext context, GeocodingHandler handler) =>
                handler.HandleBatchGeocodeAsync(context, locatorName: null, TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context)))
            .WithDisplayName("Batch Geocode Addresses (Alias)")
            .WithName("BatchGeocodeAddressesAlias")
            .WithTags("GeocodeServer");

        return endpoints;
    }

    private static Task<IResult> HandleMetadata(
        HttpContext context,
        string locatorName,
        GeocodingHandler handler)
        => handler.HandleMetadataAsync(
            context,
            locatorName,
            TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> HandleFindAddressCandidates(
        HttpContext context,
        string locatorName,
        GeocodingHandler handler)
        => handler.HandleFindAddressCandidatesAsync(
            context,
            locatorName,
            TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> HandleReverseGeocode(
        HttpContext context,
        string locatorName,
        GeocodingHandler handler)
        => handler.HandleReverseGeocodeAsync(
            context,
            locatorName,
            TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> HandleSuggest(
        HttpContext context,
        string locatorName,
        GeocodingHandler handler)
        => handler.HandleSuggestAsync(
            context,
            locatorName,
            TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));

    private static Task<IResult> HandleBatch(
        HttpContext context,
        string locatorName,
        GeocodingHandler handler)
        => handler.HandleBatchGeocodeAsync(
            context,
            locatorName,
            TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context));
}
