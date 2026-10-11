// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Models;
using Honua.Protocols.GeoServices.GPServer;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Protocols.GeoServices.NAServer;
using Honua.Protocols.GeoServices.NAServer.Models;
using Honua.Routing.Features.Routing.Abstractions;
using Honua.Routing.Features.Routing.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.GeoServices;

/// <summary>
/// Projects the canonical service-area solve as a bounded synchronous GP tool.
/// This adapter does not establish stock arcpy.nax binding or an asynchronous job contract.
/// </summary>
internal static class GenerateServiceAreasWebTool
{
    private static readonly HashSet<string> AllowedParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "f", "token", "Facilities", "Break_Values", "defaultBreaks", "Break_Units",
        "Travel_Direction", "travelDirection", "Travel_Mode", "travelMode", "inSR", "outSR",
        "Point_Barriers", "Line_Barriers", "Polygon_Barriers", "barriers", "polylineBarriers", "polygonBarriers",
    };

    internal static async Task<IResult> ExecuteAsync(HttpContext context, CancellationToken ct)
    {
        ct = TimeoutTokenHelper.GetTimeoutAwareCancellationToken(context);
        var parameters = await GPServerParameterTranslation.ReadRequestParametersAsync(context, ct).ConfigureAwait(false);
        parameters.TryGetValue("f", out var format);
        if (!string.IsNullOrWhiteSpace(format) &&
            !format.Equals("json", StringComparison.OrdinalIgnoreCase) &&
            !format.Equals("pjson", StringComparison.OrdinalIgnoreCase) &&
            !format.Equals("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Unsupported output format. Use f=json.");
        }

        var routing = context.RequestServices.GetRequiredService<IRoutingProvider>();
        var configuration = context.RequestServices.GetRequiredService<IOptions<RoutingConfiguration>>().Value;
        var capabilities = await routing.GetCapabilitiesAsync(ct).ConfigureAwait(false);
        if (!capabilities.SupportsServiceArea)
        {
            return StandardErrorHelpers.CreateBadRequest(context, "Service-area solves are not supported by the configured routing provider.");
        }

        try
        {
            var request = NAServerParameterTranslation.BuildServiceAreaSolveRequest(
                TranslateParameters(parameters), NAServerInputCaps.FromConfiguration(configuration));
            var facilities = ReadFacilityIdentities(parameters, request.Facilities.Count);
            if (!capabilities.SupportedTravelDirections.Contains(request.TravelDirection))
            {
                return StandardErrorHelpers.CreateBadRequest(context, "Requested travel direction is not supported by the configured routing provider.");
            }

            var capabilityError = RoutingRequestValidation.ValidateCapabilities(capabilities, request.Barriers, request.TravelMode);
            if (capabilityError is not null)
            {
                return StandardErrorHelpers.CreateBadRequest(context, capabilityError);
            }

            var result = await routing.SolveServiceAreaAsync(request, ct).ConfigureAwait(false);
            var canonical = NAServerResultMapping.MapServiceArea(result, request.OutSrid);
            var polygons = JsonSerializer.SerializeToNode(canonical.SaPolygons, NAServerJsonContext.Default.NAServerSaPolygonsFeatureSet)!.AsObject();
            foreach (var feature in polygons["features"]!.AsArray())
            {
                var attributes = feature!["attributes"]!;
                var facilityId = attributes["FacilityID"]!.GetValue<int>();
                var identity = facilities[facilityId];
                attributes["FacilityOID"] = identity.ObjectId;
                if (identity.Name is not null)
                {
                    var canonicalName = attributes["Name"]!.GetValue<string>();
                    attributes["Name"] = identity.Name + canonicalName[$"Facility {facilityId}".Length..];
                }
            }
            polygons["fields"] = NAServerMetadata.BuildServiceAreaFields();
            var messages = (canonical.Messages ?? []).Select(message => new GPJobMessage
            {
                Type = message.Type >= 100 ? "esriJobMessageTypeError"
                    : message.Type >= 50 ? "esriJobMessageTypeWarning" : "esriJobMessageTypeInformative",
                Description = message.Description,
            }).ToArray();
            var document = new JsonObject
            {
                ["results"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["paramName"] = "Service_Areas",
                        ["dataType"] = "GPFeatureRecordSetLayer",
                        ["value"] = polygons,
                    },
                    new JsonObject
                    {
                        ["paramName"] = "Solve_Succeeded",
                        ["dataType"] = "GPBoolean",
                        ["value"] = result.Polygons.Count > 0,
                    },
                },
                ["messages"] = JsonSerializer.SerializeToNode(messages, GPServerJsonContext.Default.GPJobMessageArray),
            };
            return Results.Text(NAServerMetadata.Serialize(document, string.Equals(format, "pjson", StringComparison.OrdinalIgnoreCase)), "application/json");
        }
        catch (NAServerParameterTranslation.NAServerParameterException exception)
        {
            return StandardErrorHelpers.CreateBadRequest(context, exception.Message);
        }
    }

    private sealed record FacilityIdentity(int? ObjectId, string? Name);

    private static IReadOnlyList<FacilityIdentity> ReadFacilityIdentities(
        IReadOnlyDictionary<string, string> parameters, int count)
    {
        var identities = Enumerable.Repeat(new FacilityIdentity(null, null), count).ToArray();
        var raw = parameters["Facilities"].Trim();
        if (!raw.StartsWith('{'))
        {
            return identities;
        }

        // Canonical parsing has already validated the JSON. Require each FeatureSet row
        // to remain a point so skipped geometries cannot shift identity-to-facility joins.
        using var document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("features", out var features))
        {
            return identities;
        }

        if (features.ValueKind != JsonValueKind.Array || features.GetArrayLength() != count)
        {
            throw new NAServerParameterTranslation.NAServerParameterException("Facilities must contain one valid point geometry per feature.");
        }

        var index = 0;
        foreach (var feature in features.EnumerateArray())
        {
            if (!feature.TryGetProperty("geometry", out var geometry) || geometry.ValueKind != JsonValueKind.Object ||
                !geometry.TryGetProperty("x", out var x) || !geometry.TryGetProperty("y", out var y) ||
                x.ValueKind != JsonValueKind.Number || y.ValueKind != JsonValueKind.Number ||
                !double.IsFinite(x.GetDouble()) || !double.IsFinite(y.GetDouble()))
            {
                throw new NAServerParameterTranslation.NAServerParameterException("Facilities must contain one valid point geometry per feature.");
            }

            int? objectId = null;
            string? name = null;
            if (feature.TryGetProperty("attributes", out var attributes) && attributes.ValueKind != JsonValueKind.Null)
            {
                if (attributes.ValueKind != JsonValueKind.Object)
                {
                    throw new NAServerParameterTranslation.NAServerParameterException("Facilities attributes must be an object.");
                }

                foreach (var attribute in attributes.EnumerateObject())
                {
                    var value = attribute.Value;
                    if (attribute.Name.Equals("ObjectID", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.ValueKind != JsonValueKind.Null)
                        {
                            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var oid))
                            {
                                throw new NAServerParameterTranslation.NAServerParameterException("Facility ObjectID must be a 32-bit integer.");
                            }
                            objectId = oid;
                        }
                    }
                    else if (attribute.Name.Equals("Name", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.ValueKind != JsonValueKind.Null)
                        {
                            if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > 500)
                            {
                                throw new NAServerParameterTranslation.NAServerParameterException("Facility Name must be a string of at most 500 characters.");
                            }
                            name = value.GetString();
                        }
                    }
                    else if (attribute.Name.Equals("Breaks", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.ValueKind != JsonValueKind.Null &&
                            (value.ValueKind != JsonValueKind.String || !string.IsNullOrWhiteSpace(value.GetString())))
                        {
                            throw new NAServerParameterTranslation.NAServerParameterException("Per-facility Breaks overrides are not supported; use Break_Values.");
                        }
                    }
                    else if (attribute.Name.Equals("AdditionalTime", StringComparison.OrdinalIgnoreCase) ||
                             attribute.Name.Equals("AdditionalDistance", StringComparison.OrdinalIgnoreCase) ||
                             attribute.Name.Equals("AdditionalCost", StringComparison.OrdinalIgnoreCase) ||
                             attribute.Name.Equals("CurbApproach", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.ValueKind != JsonValueKind.Null &&
                            (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || number != 0))
                        {
                            throw new NAServerParameterTranslation.NAServerParameterException($"Facility {attribute.Name} supports only its default zero value.");
                        }
                    }
                    else
                    {
                        throw new NAServerParameterTranslation.NAServerParameterException($"Facility attribute '{attribute.Name}' is not supported. Use ObjectID and Name.");
                    }
                }
            }
            identities[index++] = new FacilityIdentity(objectId, name);
        }
        return identities;
    }

    private static Dictionary<string, string> TranslateParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var translated = new Dictionary<string, string>(parameters, StringComparer.OrdinalIgnoreCase);
        foreach (var key in translated.Keys)
        {
            if (!AllowedParameters.Contains(key))
            {
                throw new NAServerParameterTranslation.NAServerParameterException($"GenerateServiceAreas parameter '{key}' is not supported by this minutes-only tool.");
            }
        }

        if (translated.TryGetValue("Break_Units", out var units) && !units.Equals("Minutes", StringComparison.OrdinalIgnoreCase))
        {
            throw new NAServerParameterTranslation.NAServerParameterException("GenerateServiceAreas Break_Units supports only Minutes.");
        }

        if (translated.TryGetValue("Travel_Direction", out var direction))
        {
            direction = direction.Trim().Equals("Away From Facility", StringComparison.OrdinalIgnoreCase)
                ? "esriNATravelDirectionFromFacility"
                : direction.Trim().Equals("Towards Facility", StringComparison.OrdinalIgnoreCase)
                    ? "esriNATravelDirectionToFacility"
                    : throw new NAServerParameterTranslation.NAServerParameterException("Travel_Direction must be Away From Facility or Towards Facility.");
            if (translated.TryGetValue("travelDirection", out var other) &&
                NAServerParameterTranslation.ParseTravelDirection(other) != NAServerParameterTranslation.ParseTravelDirection(direction))
            {
                throw new NAServerParameterTranslation.NAServerParameterException("Travel_Direction and travelDirection conflict.");
            }
            translated["travelDirection"] = direction;
        }

        foreach (var key in new[] { "Break_Values", "defaultBreaks" })
        {
            if (translated.TryGetValue(key, out var rawBreaks))
            {
                var parts = rawBreaks.Split([' ', ',', ';', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (parts.Length == 0 || parts.Any(part => !double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value) || value <= 0))
                {
                    throw new NAServerParameterTranslation.NAServerParameterException("GenerateServiceAreas break values must be finite positive minutes.");
                }
                translated[key] = string.Join(",", parts);
            }
        }

        foreach (var (source, target) in new[]
                 {
                     ("Break_Values", "defaultBreaks"), ("Travel_Mode", "travelMode"),
                     ("Point_Barriers", "barriers"), ("Line_Barriers", "polylineBarriers"), ("Polygon_Barriers", "polygonBarriers"),
                 })
        {
            if (!translated.TryGetValue(source, out var value))
            {
                continue;
            }

            if (translated.TryGetValue(target, out var other) && !string.Equals(value, other, StringComparison.Ordinal))
            {
                throw new NAServerParameterTranslation.NAServerParameterException($"GenerateServiceAreas parameters '{source}' and '{target}' conflict.");
            }
            translated[target] = value;
        }

        return translated;
    }
}
