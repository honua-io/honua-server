// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.Infrastructure.SharedCaching;
using Microsoft.Extensions.DependencyInjection;
using static Honua.Protocols.Ogc.Classic.OgcClassicRequestHelpers;

namespace Honua.Protocols.Ogc.Classic.Wms;

// Per-service "challenge" presentation of layers the caller may not read (service option
// wmsProtectedLayers). The default hides them; challenge mode names them in capabilities and
// answers a request that names one with the same access denial a fully protected service gives.
internal static partial class WmsRequestHandlers
{
    // Layers the capabilities document lists: the readable ones in full, the challenge ones by name.
    private readonly record struct WmsCapabilitiesLayers(IReadOnlyList<WmsLayer> Accessible, IReadOnlyList<WmsLayer> Challenge);

    // The layer parameters each operation actually uses; a stray parameter it ignores is not a request for that layer.
    private static readonly Dictionary<string, string[]> _layerParametersByRequest = new(StringComparer.OrdinalIgnoreCase)
    {
        ["GetMap"] = ["LAYERS"],
        ["GetFeatureInfo"] = ["LAYERS", "QUERY_LAYERS"],
        ["GetLegendGraphic"] = ["LAYER"],
    };

    // Only tenant-visible layers reach this point (ResolveWmsLayers drops the rest), so challenge
    // mode never names a layer of another tenant.
    private static WmsLayer[] ResolveWmsChallengeLayers(
        MetadataV2Service service,
        IReadOnlyList<WmsLayer> tenantVisibleLayers,
        IReadOnlyList<WmsLayer> accessibleLayers)
    {
        if (!MetadataV2WmsProtectedLayers.ChallengesProtectedLayers(service))
        {
            return [];
        }

        var accessible = accessibleLayers.Select(static layer => layer.Identifier).ToHashSet(StringComparer.Ordinal);
        return [.. tenantVisibleLayers.Where(layer => !accessible.Contains(layer.Identifier))];
    }

    private static IResult? ChallengeRequestedProtectedLayers(
        HttpContext context,
        MetadataV2Service service,
        IReadOnlyList<WmsLayer> challengeLayers)
    {
        var requestType = NormalizeWmsRequestType(GetQueryValue(context.Request.Query, "REQUEST"));
        if (challengeLayers.Count == 0 || requestType is null
            || !_layerParametersByRequest.TryGetValue(requestType, out var parameters))
        {
            return null;
        }

        var requested = parameters
            .Select(name => GetQueryValue(context.Request.Query, name))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(static value => value!.Split(',', StringSplitOptions.TrimEntries))
            .ToHashSet(StringComparer.Ordinal);
        var denial = challengeLayers
            .Where(layer => requested.Contains(layer.Identifier))
            .Select(layer => AccessPolicyHelpers.EvaluateResourceAccess(context, layer.Resource, service))
            .Where(static decision => !decision.IsAllowed)
            .OrderByDescending(static decision => decision.RequiresAuthentication)
            .Select(static decision => (AccessDecision?)decision)
            .FirstOrDefault();
        return denial is { } refused ? CreateWmsAccessDenied(context, refused) : null;
    }

    // An access denial is always an XML ServiceExceptionReport with its real status: an INIMAGE or
    // BLANK exception format would turn the refusal into a 200 image and drop the 401/403 contract.
    private static IResult CreateWmsAccessDenied(HttpContext context, AccessDecision decision)
    {
        ResponseAccessVerdict.Record(context, decision);
        if (decision.RequiresAuthentication)
        {
            AccessPolicyHelpers.AppendAuthenticationChallenge(context);
        }

        var (message, status) = decision.RequiresAuthentication
            ? (AccessPolicyHelpers.AuthRequiredMessage, StatusCodes.Status401Unauthorized)
            : (AccessPolicyHelpers.AccessForbiddenMessage, StatusCodes.Status403Forbidden);
        return CreateWmsAccessDeniedReport(context, message, status);
    }

    // The service-level gate has already appended any challenge header and recorded the verdict. Its
    // status rule is unchanged: a signed-in caller is refused (403), anyone else is asked to sign in (401).
    private static IResult CreateWmsAccessDenied(HttpContext context)
        => context.User?.Identity?.IsAuthenticated == true
            ? CreateWmsAccessDeniedReport(context, AccessPolicyHelpers.AccessForbiddenMessage, StatusCodes.Status403Forbidden)
            : CreateWmsAccessDeniedReport(context, AccessPolicyHelpers.AuthRequiredMessage, StatusCodes.Status401Unauthorized);

    private static IResult CreateWmsAccessDeniedReport(HttpContext context, string message, int status)
    {
        context.RequestServices.GetService<RecentErrorBuffer>()?.Record(context, status, "AccessDenied", message, includeClientErrors: true);
        var exceptionsValue = GetQueryValue(context.Request.Query, "EXCEPTIONS");
        var xmlExceptions = IsWmsImageExceptionFormat(exceptionsValue) ? null : exceptionsValue;
        var version = GetRequestedWmsVersion(context.Request.Query);
        var xml = BuildWmsServiceExceptionReport("AccessDenied", message, version);
        return Results.Content(xml, GetWmsExceptionMimeType(xmlExceptions, version), Encoding.UTF8, status);
    }

    // A challenge layer is named, never described: no title, abstract, keywords, extent, dimension
    // or legend comes from the protected resource, and nothing is read from its storage. It declares
    // the WGS 84 CRS itself because the root may declare none when the readable layers share none.
    private static void AppendWmsChallengeLayers(StringBuilder sb, IReadOnlyList<WmsLayer> challengeLayers, string version)
    {
        foreach (var layer in challengeLayers)
        {
            var layerName = EscapeXml(GetWmsLayerDisplayName(layer));
            sb.AppendLine("      <Layer queryable=\"0\">");
            sb.Append("        <Name>").Append(layerName).AppendLine("</Name>");
            sb.Append("        <Title>").Append(layerName).AppendLine("</Title>");
            sb.AppendLine(IsWms111Version(version) ? "        <SRS>EPSG:4326</SRS>" : "        <CRS>CRS:84</CRS>");
            sb.AppendLine("      </Layer>");
        }
    }
}
