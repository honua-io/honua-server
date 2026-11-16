// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Core.Features.Studio.Drafts;

public sealed partial class MapPackageDraftFactory
{
    public const string MapPackageFormat = "honua_map_package.v1";

    public const string IdentifierPrefix = "map";

    public const string DefaultCrs = "EPSG:4326";

    public const string UnresolvedLocatorScheme = "honua://unresolved/";

    private const string OpenGisCrsPrefix = "http://www.opengis.net/def/crs/";
    private const string OgcUrnCrsPrefix = "urn:ogc:def:crs:";

    private readonly IDraftIdentifierGenerator _identifiers;
    private readonly TimeProvider _timeProvider;

    public MapPackageDraftFactory(IDraftIdentifierGenerator identifiers, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(identifiers);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _identifiers = identifiers;
        _timeProvider = timeProvider;
    }

    public MapPackageDraftResult CreateDraft(MapPackageDraftRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var errors = new List<PackageDraftFinding>();
        var warnings = new List<PackageDraftFinding>();

        var sourceBindings = BuildSourceBindings(request.SourceBindings, errors, warnings);
        var styleRefs = BuildStyleRefs(request.StyleId, errors);
        var initialView = BuildInitialView(request.InitialView, errors);
        var templateId = NormalizeOptional(request.TemplateId, "templateId", "/templateId", errors);
        var themeId = NormalizeOptional(request.ThemeId, "themeId", "/themeId", errors);

        if (errors.Count > 0)
        {
            return new MapPackageDraftResult { Errors = errors, Warnings = warnings };
        }

        var package = new MapPackage
        {
            MapPackageId = _identifiers.NewIdentifier(IdentifierPrefix),

            // Server-owned discriminators (retained knowledge §5): never caller supplied.
            Format = MapPackageFormat,
            Status = PackageStatus.Draft,

            TemplateId = templateId,
            SourceBindings = sourceBindings,
            StyleRefs = styleRefs,
            ThemeId = themeId,
            InitialView = initialView,
            CreatedAt = _timeProvider.GetUtcNow()
        };

        return new MapPackageDraftResult { Package = package, Warnings = warnings };
    }

    private static List<SourceBinding> BuildSourceBindings(
        IReadOnlyList<SourceBindingInput> inputs,
        List<PackageDraftFinding> errors,
        List<PackageDraftFinding> warnings)
    {
        if (inputs.Count == 0)
        {
            return [];
        }

        var bindings = new List<SourceBinding>(inputs.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < inputs.Count; index++)
        {
            var path = $"/sourceBindings/{index}";
            var input = inputs[index];
            if (input is null)
            {
                errors.Add(Finding("sourceBindingMissing", path, "A source binding entry must be an object."));
                continue;
            }

            var sourceId = input.SourceId?.Trim();
            if (string.IsNullOrEmpty(sourceId))
            {
                errors.Add(Finding("sourceIdMissing", path + "/sourceId", "A source binding requires a non-empty sourceId."));
                continue;
            }

            if (!seen.Add(sourceId))
            {
                errors.Add(Finding(
                    "sourceIdDuplicate",
                    path + "/sourceId",
                    $"sourceId '{sourceId}' is used by more than one source binding."));
                continue;
            }

            if (!TryParseProtocol(input.Protocol, out var protocol))
            {
                errors.Add(Finding(
                    "sourceProtocolInvalid",
                    path + "/protocol",
                    $"protocol '{input.Protocol}' is not a supported source protocol. Supported: {SupportedProtocols}."));
                continue;
            }

            var url = input.Url?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                // Retained knowledge §2: a missing locator URL is deliberately a
                // warning, so a descriptively named layer still yields a draft.
                url = UnresolvedLocatorScheme + sourceId;
                warnings.Add(Finding(
                    "sourceNotResolved",
                    path + "/url",
                    $"Source '{sourceId}' has no locator URL; it was recorded as '{url}' and must be resolved before publish."));
            }

            bindings.Add(new SourceBinding
            {
                SourceId = sourceId,
                Protocol = protocol,
                Locator = new SourceLocator
                {
                    Url = url,
                    ServiceId = NullIfBlank(input.ServiceId),
                    LayerId = NullIfBlank(input.LayerId)
                },
                Filter = NullIfBlank(input.Filter),
                Metadata = input.Metadata is null
                    ? new Dictionary<string, string>(StringComparer.Ordinal)
                    : new Dictionary<string, string>(input.Metadata, StringComparer.Ordinal)
            });
        }

        return bindings;
    }

    private static IReadOnlyList<StyleRef> BuildStyleRefs(string? styleId, List<PackageDraftFinding> errors)
    {
        if (styleId is null)
        {
            return [];
        }

        // Retained knowledge §2: a present styleRef with a blank styleId is a
        // blocking error — a half-specified reference is worse than none.
        var trimmed = styleId.Trim();
        if (trimmed.Length == 0)
        {
            errors.Add(Finding("styleRefInvalid", "/styleId", "styleId was supplied but is blank; omit it or supply a style identifier."));
            return [];
        }

        return [new StyleRef { StyleId = trimmed }];
    }

    private static MapInitialView? BuildInitialView(MapInitialViewInput? input, List<PackageDraftFinding> errors)
    {
        if (input is null)
        {
            return null;
        }

        var errorsBefore = errors.Count;
        var crs = DefaultCrs;
        if (input.Crs is not null)
        {
            var trimmed = input.Crs.Trim();
            if (trimmed.Length == 0)
            {
                errors.Add(Finding("crsMissing", "/initialView/crs", "crs was supplied but is blank; omit it to default to " + DefaultCrs + "."));
            }
            else if (!IsSupportedCrs(trimmed))
            {
                errors.Add(Finding(
                    "crsUnsupported",
                    "/initialView/crs",
                    $"crs '{trimmed}' is not supported. Use EPSG:<code>, {OpenGisCrsPrefix}…, or {OgcUrnCrsPrefix}…."));
            }
            else
            {
                crs = trimmed;
            }
        }

        var bbox = input.Bbox;
        if (bbox is null || bbox.Count != 4)
        {
            errors.Add(Finding(
                "bboxInvalid",
                "/initialView/bbox",
                "bbox must contain exactly four numbers ordered [minLon, minLat, maxLon, maxLat]."));
            return null;
        }

        for (var index = 0; index < 4; index++)
        {
            if (!double.IsFinite(bbox[index]))
            {
                errors.Add(Finding(
                    "bboxInvalid",
                    $"/initialView/bbox/{index}",
                    "bbox values must be finite numbers."));
                return null;
            }
        }

        if (bbox[0] > bbox[2])
        {
            errors.Add(Finding(
                "bboxNotOrdered",
                "/initialView/bbox",
                $"bbox minLon ({bbox[0]}) must be less than or equal to maxLon ({bbox[2]}); the axis order is [minLon, minLat, maxLon, maxLat]."));
        }

        if (bbox[1] > bbox[3])
        {
            errors.Add(Finding(
                "bboxNotOrdered",
                "/initialView/bbox",
                $"bbox minLat ({bbox[1]}) must be less than or equal to maxLat ({bbox[3]}); the axis order is [minLon, minLat, maxLon, maxLat]."));
        }

        return errors.Count > errorsBefore
            ? null
            : new MapInitialView { Bbox = [bbox[0], bbox[1], bbox[2], bbox[3]], Crs = crs };
    }

    private static bool IsSupportedCrs(string crs) =>
        EpsgCrsPattern().IsMatch(crs)
        || (crs.StartsWith(OpenGisCrsPrefix, StringComparison.Ordinal) && crs.Length > OpenGisCrsPrefix.Length)
        || (crs.StartsWith(OgcUrnCrsPrefix, StringComparison.Ordinal) && crs.Length > OgcUrnCrsPrefix.Length);

    [GeneratedRegex("^EPSG:[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex EpsgCrsPattern();

    private const string SupportedProtocols =
        "geoservices_feature_service, geoservices_map_service, ogc_features, ogc_maps, ogc_tiles, wfs, wms, "
        + "odata, vector_tile, raster_tile, workspace_artifact, pmtiles";

    private static bool TryParseProtocol(string? value, out SourceProtocol protocol)
    {
        protocol = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        switch (value.Trim())
        {
            case "geoservices_feature_service": protocol = SourceProtocol.GeoservicesFeatureService; return true;
            case "geoservices_map_service": protocol = SourceProtocol.GeoservicesMapService; return true;
            case "ogc_features": protocol = SourceProtocol.OgcFeatures; return true;
            case "ogc_maps": protocol = SourceProtocol.OgcMaps; return true;
            case "ogc_tiles": protocol = SourceProtocol.OgcTiles; return true;
            case "wfs": protocol = SourceProtocol.Wfs; return true;
            case "wms": protocol = SourceProtocol.Wms; return true;
            case "odata": protocol = SourceProtocol.OData; return true;
            case "vector_tile": protocol = SourceProtocol.VectorTile; return true;
            case "raster_tile": protocol = SourceProtocol.RasterTile; return true;
            case "workspace_artifact": protocol = SourceProtocol.WorkspaceArtifact; return true;
            case "pmtiles": protocol = SourceProtocol.PMTiles; return true;
            default: return false;
        }
    }

    private static string? NormalizeOptional(
        string? value,
        string name,
        string path,
        List<PackageDraftFinding> errors)
    {
        if (value is null)
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != 0)
        {
            return trimmed;
        }

        errors.Add(Finding(name + "Invalid", path, $"{name} was supplied but is blank; omit it instead."));
        return null;
    }

    private static string? NullIfBlank(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static PackageDraftFinding Finding(string code, string path, string message) =>
        new() { Code = code, Path = path, Message = message };
}
