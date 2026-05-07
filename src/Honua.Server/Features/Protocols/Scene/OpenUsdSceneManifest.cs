// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Scene.Domain;

namespace Honua.Server.Features.Protocols.Scene;

internal sealed record OpenUsdSceneManifest(
    string Content,
    string ETag,
    int SizeBytes);

internal sealed record OpenUsdSceneManifestInput(
    string SceneId,
    string SceneName,
    string? SceneDescription,
    bool IsProtected,
    string? Crs,
    OpenUsdSceneExtent? Extent,
    OpenUsdTilesetManifestInput Tileset,
    OpenUsdObservationsManifestInput? Observations,
    string ManifestUrl,
    string TilesetUrl,
    string? ObservationsUrl,
    string? AccessEnvelopeUrl);

internal sealed record OpenUsdSceneExtent(
    double West,
    double South,
    double East,
    double North,
    double? MinHeight,
    double? MaxHeight,
    string Source);

internal sealed class OpenUsdManifestValidationException : Exception
{
    public OpenUsdManifestValidationException(string reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    public string Reason { get; }
}

internal static class OpenUsdSceneManifestReader
{
    private const string DefaultCrs = "EPSG:4979";

    public static async Task<OpenUsdSceneManifestInput> ReadAsync(
        SceneDataset scene,
        SceneDatasetRecordView? record,
        string manifestUrl,
        string tilesetUrl,
        string? accessEnvelopeUrl,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scene);

        if (!SceneAssetResolver.TryResolve(scene, scene.TilesetFileName, out var tilesetAsset, out var tilesetError))
        {
            throw new OpenUsdManifestValidationException(
                "tileset_not_found",
                tilesetError == SceneAssetResolutionError.NotFound
                    ? "Scene tileset.json was not found."
                    : "Scene tileset path is invalid.");
        }

        OpenUsdTilesetManifestInput? tileset;
        await using (var stream = tilesetAsset.File.OpenRead())
        {
            try
            {
                tileset = await JsonSerializer.DeserializeAsync(
                    stream,
                    OpenUsdSceneJsonContext.Default.OpenUsdTilesetManifestInput,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new OpenUsdManifestValidationException(
                    "tileset_json_invalid",
                    $"Scene tileset.json could not be parsed: {ex.Message}");
            }
        }

        if (tileset is null)
        {
            throw new OpenUsdManifestValidationException(
                "tileset_json_empty",
                "Scene tileset.json was empty.");
        }

        var extent = ResolveExtent(record?.Extent, tileset);
        var observations = await ResolveObservationsAsync(scene, tileset, tilesetUrl, cancellationToken)
            .ConfigureAwait(false);

        ValidateTileset(tileset);
        ValidateExtent(extent);
        ValidateObservations(observations);

        return new OpenUsdSceneManifestInput(
            SceneId: scene.Id,
            SceneName: scene.Name,
            SceneDescription: scene.Description,
            IsProtected: scene.Metadata?.AccessPolicy is not null,
            Crs: string.IsNullOrWhiteSpace(record?.Crs) ? DefaultCrs : record!.Crs,
            Extent: extent,
            Tileset: tileset,
            Observations: observations,
            ManifestUrl: manifestUrl,
            TilesetUrl: tilesetUrl,
            ObservationsUrl: observations is null ? null : BuildSiblingUrl(tilesetUrl, observations.SidecarUri!),
            AccessEnvelopeUrl: accessEnvelopeUrl);
    }

    private static OpenUsdSceneExtent? ResolveExtent(
        SceneExtent? recordExtent,
        OpenUsdTilesetManifestInput tileset)
    {
        if (recordExtent is not null)
        {
            return new OpenUsdSceneExtent(
                recordExtent.XMin,
                recordExtent.YMin,
                recordExtent.XMax,
                recordExtent.YMax,
                MinHeight: null,
                MaxHeight: null,
                Source: "scene-registry");
        }

        if (tileset.Extras?.Bounds is { } bounds)
        {
            return new OpenUsdSceneExtent(
                bounds.West,
                bounds.South,
                bounds.East,
                bounds.North,
                bounds.MinHeight,
                bounds.MaxHeight,
                Source: "tileset.extras.bounds");
        }

        var region = tileset.Root?.BoundingVolume?.Region;
        if (region is { Count: 6 })
        {
            return new OpenUsdSceneExtent(
                RadiansToDegrees(region[0]),
                RadiansToDegrees(region[1]),
                RadiansToDegrees(region[2]),
                RadiansToDegrees(region[3]),
                region[4],
                region[5],
                Source: "tileset.root.boundingVolume.region");
        }

        return null;
    }

    private static async Task<OpenUsdObservationsManifestInput?> ResolveObservationsAsync(
        SceneDataset scene,
        OpenUsdTilesetManifestInput tileset,
        string tilesetUrl,
        CancellationToken cancellationToken)
    {
        var sidecarUri = tileset.Extras?.ObservationsLayer?.SidecarUri
            ?? tileset.Extras?.ObservationsSidecar?.Uri;
        if (string.IsNullOrWhiteSpace(sidecarUri))
        {
            return null;
        }

        if (!IsSafeSourceUri(sidecarUri))
        {
            throw new OpenUsdManifestValidationException(
                "observations_sidecar_uri_invalid",
                "Observation sidecar URI is not a safe relative scene asset URI.");
        }

        if (!SceneAssetResolver.TryResolve(scene, sidecarUri, out var resolved, out var error))
        {
            throw new OpenUsdManifestValidationException(
                "observations_sidecar_missing",
                error == SceneAssetResolutionError.NotFound
                    ? "Observation sidecar was declared but could not be found."
                    : "Observation sidecar path is invalid.");
        }

        OpenUsdObservationsManifestInput? observations;
        await using (var stream = resolved.File.OpenRead())
        {
            try
            {
                observations = await JsonSerializer.DeserializeAsync(
                    stream,
                    OpenUsdSceneJsonContext.Default.OpenUsdObservationsManifestInput,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (JsonException ex)
            {
                throw new OpenUsdManifestValidationException(
                    "observations_sidecar_invalid",
                    $"Observation sidecar could not be parsed: {ex.Message}");
            }
        }

        if (observations is null)
        {
            throw new OpenUsdManifestValidationException(
                "observations_sidecar_empty",
                "Observation sidecar was declared but was empty.");
        }

        observations.SidecarUri = sidecarUri;
        observations.SidecarUrl = BuildSiblingUrl(tilesetUrl, sidecarUri);
        return observations;
    }

    private static void ValidateTileset(OpenUsdTilesetManifestInput tileset)
    {
        if (!string.Equals(tileset.Asset?.Version, "1.1", StringComparison.Ordinal))
        {
            throw new OpenUsdManifestValidationException(
                "tileset_version_unsupported",
                "Only OGC 3D Tiles 1.1 root documents are supported by the OpenUSD preview manifest.");
        }
    }

    private static void ValidateExtent(OpenUsdSceneExtent? extent)
    {
        if (extent is null)
        {
            return;
        }

        if (!IsLongitude(extent.West) || !IsLongitude(extent.East) ||
            !IsLatitude(extent.South) || !IsLatitude(extent.North) ||
            extent.West > extent.East ||
            extent.South > extent.North)
        {
            throw new OpenUsdManifestValidationException(
                "coordinate_metadata_invalid",
                "Scene extent metadata must use WGS-84 longitude/latitude ranges.");
        }

        if (extent.MinHeight is { } minHeight &&
            extent.MaxHeight is { } maxHeight &&
            minHeight > maxHeight)
        {
            throw new OpenUsdManifestValidationException(
                "coordinate_metadata_invalid",
                "Scene extent height metadata has minHeight greater than maxHeight.");
        }
    }

    private static void ValidateObservations(OpenUsdObservationsManifestInput? observations)
    {
        if (observations?.Observations is null)
        {
            return;
        }

        foreach (var observation in observations.Observations)
        {
            if (string.IsNullOrWhiteSpace(observation.Id))
            {
                throw new OpenUsdManifestValidationException(
                    "observations_sidecar_invalid",
                    "Observation sidecar contains an observation without an id.");
            }

            if (!IsLongitude(observation.Longitude) || !IsLatitude(observation.Latitude))
            {
                throw new OpenUsdManifestValidationException(
                    "coordinate_metadata_invalid",
                    "Observation sidecar contains coordinates outside WGS-84 longitude/latitude ranges.");
            }
        }
    }

    private static string BuildSiblingUrl(string tilesetUrl, string relativeUri)
    {
        var slash = tilesetUrl.LastIndexOf('/');
        var prefix = slash < 0 ? tilesetUrl : tilesetUrl[..(slash + 1)];
        return prefix + relativeUri;
    }

    private static bool IsSafeSourceUri(string uri)
    {
        if (uri.Length == 0 ||
            uri[0] == '/' ||
            uri[0] == '\\' ||
            uri.Contains('\\', StringComparison.Ordinal) ||
            uri.Contains('\0', StringComparison.Ordinal) ||
            uri.Contains('?', StringComparison.Ordinal) ||
            uri.Contains('#', StringComparison.Ordinal) ||
            uri.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        return !uri.Split('/', StringSplitOptions.None)
            .Any(segment => segment.Length == 0 || segment == "." || segment == "..");
    }

    private static bool IsLongitude(double value) => value is >= -180d and <= 180d;

    private static bool IsLatitude(double value) => value is >= -90d and <= 90d;

    private static double RadiansToDegrees(double radians) => radians * 180d / Math.PI;

    public sealed record SceneDatasetRecordView(
        SceneExtent? Extent,
        string? Crs);
}

internal static class OpenUsdSceneManifestWriter
{
    public const string ExportSchemaVersion = "1.0";
    private const string RootPrimName = "HonuaScene";
    private const string VerticalDatumUnknown = "unknown";

    public static OpenUsdSceneManifest Write(OpenUsdSceneManifestInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        var builder = new StringBuilder();
        builder.AppendLine("#usda 1.0");
        builder.AppendLine("(");
        builder.AppendLine($"    defaultPrim = {UsdString(RootPrimName)}");
        builder.AppendLine("    metersPerUnit = 1");
        builder.AppendLine("    upAxis = \"Z\"");
        builder.AppendLine("    customLayerData = {");
        WriteString(builder, "honua:exportSchemaVersion", ExportSchemaVersion, indent: 8);
        WriteString(builder, "honua:sceneId", input.SceneId, indent: 8);
        WriteString(builder, "honua:sceneName", input.SceneName, indent: 8);
        WriteString(builder, "honua:sourceTilesetUrl", input.TilesetUrl, indent: 8);
        WriteString(builder, "honua:sourceManifestUrl", input.ManifestUrl, indent: 8);
        WriteString(builder, "honua:crs", input.Crs, indent: 8);
        WriteString(builder, "honua:verticalDatum", VerticalDatumUnknown, indent: 8);
        WriteString(builder, "honua:access", input.IsProtected ? "protected" : "public", indent: 8);
        builder.AppendLine("    }");
        builder.AppendLine(")");
        builder.AppendLine();

        builder.AppendLine($"def Scope {UsdString(RootPrimName)} (");
        builder.AppendLine("    customData = {");
        WriteString(builder, "honua:sceneId", input.SceneId, indent: 8);
        WriteString(builder, "honua:name", input.SceneName, indent: 8);
        WriteString(builder, "honua:description", input.SceneDescription, indent: 8);
        WriteString(builder, "honua:sourceFormat", "OGC 3D Tiles", indent: 8);
        WriteString(builder, "honua:exportKind", "metadata-manifest", indent: 8);
        WriteString(builder, "honua:nativeUsdGeometry", "false", indent: 8);
        builder.AppendLine("    }");
        builder.AppendLine(")");
        builder.AppendLine("{");

        WriteSourceScope(builder, input);
        WriteExtentScope(builder, input.Extent);
        WriteProjectScope(builder, input.Tileset.Extras?.ProjectMeta);
        WriteObservationsScope(builder, input.Observations);

        builder.AppendLine("}");

        var content = builder.ToString();
        var bytes = Encoding.UTF8.GetBytes(content);
        return new OpenUsdSceneManifest(content, ComputeETag(bytes), bytes.Length);
    }

    private static void WriteSourceScope(StringBuilder builder, OpenUsdSceneManifestInput input)
    {
        builder.AppendLine("    def Scope \"Source3DTiles\" (");
        builder.AppendLine("        customData = {");
        WriteString(builder, "honua:tilesetUrl", input.TilesetUrl, indent: 12);
        WriteString(builder, "honua:tilesetAssetVersion", input.Tileset.Asset?.Version, indent: 12);
        WriteString(builder, "honua:tilesetVersion", input.Tileset.Asset?.TilesetVersion, indent: 12);
        WriteString(builder, "honua:tilesetGenerator", input.Tileset.Asset?.Generator, indent: 12);
        WriteString(builder, "honua:layerKind", input.Tileset.Extras?.LayerKind, indent: 12);
        WriteString(builder, "honua:layerId", input.Tileset.Extras?.LayerId, indent: 12);
        WriteString(builder, "honua:attribution", input.Tileset.Extras?.Attribution, indent: 12);
        WriteString(builder, "honua:observationsUrl", input.ObservationsUrl, indent: 12);
        WriteString(builder, "honua:accessEnvelopeUrl", input.AccessEnvelopeUrl, indent: 12);
        builder.AppendLine("        }");
        builder.AppendLine("    )");
        builder.AppendLine("    {");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void WriteExtentScope(StringBuilder builder, OpenUsdSceneExtent? extent)
    {
        if (extent is null)
        {
            return;
        }

        builder.AppendLine("    def Scope \"GeospatialMetadata\" (");
        builder.AppendLine("        customData = {");
        WriteString(builder, "honua:extentSource", extent.Source, indent: 12);
        WriteDouble(builder, "honua:west", extent.West, indent: 12);
        WriteDouble(builder, "honua:south", extent.South, indent: 12);
        WriteDouble(builder, "honua:east", extent.East, indent: 12);
        WriteDouble(builder, "honua:north", extent.North, indent: 12);
        WriteDouble(builder, "honua:minHeight", extent.MinHeight, indent: 12);
        WriteDouble(builder, "honua:maxHeight", extent.MaxHeight, indent: 12);
        WriteString(builder, "honua:heightUnit", "meter", indent: 12);
        WriteString(builder, "honua:verticalDatum", VerticalDatumUnknown, indent: 12);
        builder.AppendLine("        }");
        builder.AppendLine("    )");
        builder.AppendLine("    {");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void WriteProjectScope(StringBuilder builder, OpenUsdProjectMeta? project)
    {
        if (project is null)
        {
            return;
        }

        builder.AppendLine("    def Scope \"ProjectMetadata\" (");
        builder.AppendLine("        customData = {");
        WriteString(builder, "honua:projectId", project.Id, indent: 12);
        WriteString(builder, "honua:projectName", project.Name, indent: 12);
        WriteString(builder, "honua:projectDescription", project.Description, indent: 12);
        WriteString(builder, "honua:projectPhase", project.Phase, indent: 12);
        WriteDouble(builder, "honua:completionRatio", project.CompletionRatio, indent: 12);
        WriteString(builder, "honua:startDate", project.StartDate, indent: 12);
        WriteString(builder, "honua:expectedEndDate", project.ExpectedEndDate, indent: 12);
        WriteStringArray(builder, "honua:workPackageIds", project.WorkPackages?.Select(item => item.Id), indent: 12);
        WriteStringArray(builder, "honua:workPackageNames", project.WorkPackages?.Select(item => item.Name), indent: 12);
        WriteStringArray(builder, "honua:parcelIds", project.Parcels?.Select(item => item.Id), indent: 12);
        WriteStringArray(builder, "honua:parcelNames", project.Parcels?.Select(item => item.Name), indent: 12);
        WriteStringArray(builder, "honua:stakeholderRoles", project.Stakeholders?.Select(item => item.Role), indent: 12);
        builder.AppendLine("        }");
        builder.AppendLine("    )");
        builder.AppendLine("    {");
        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void WriteObservationsScope(StringBuilder builder, OpenUsdObservationsManifestInput? observations)
    {
        if (observations is null)
        {
            return;
        }

        builder.AppendLine("    def Scope \"Observations\" (");
        builder.AppendLine("        customData = {");
        WriteString(builder, "honua:sidecarUri", observations.SidecarUri, indent: 12);
        WriteString(builder, "honua:sidecarUrl", observations.SidecarUrl, indent: 12);
        WriteString(builder, "honua:schemaVersion", observations.SchemaVersion, indent: 12);
        WriteString(builder, "honua:projectId", observations.ProjectId, indent: 12);
        WriteString(builder, "honua:generatedAt", observations.GeneratedAt, indent: 12);
        WriteInt(builder, "honua:observationCount", observations.Observations?.Count ?? 0, indent: 12);
        builder.AppendLine("        }");
        builder.AppendLine("    )");
        builder.AppendLine("    {");

        foreach (var observation in (observations.Observations ?? Enumerable.Empty<OpenUsdObservation>())
            .OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            var primName = UsdString(ToUsdIdentifier("Observation_" + observation.Id));
            builder.AppendLine($"        def Scope {primName} (");
            builder.AppendLine("            customData = {");
            WriteString(builder, "honua:id", observation.Id, indent: 16);
            WriteString(builder, "honua:kind", observation.Kind, indent: 16);
            WriteString(builder, "honua:status", observation.Status, indent: 16);
            WriteString(builder, "honua:severity", observation.Severity, indent: 16);
            WriteString(builder, "honua:recordedAt", observation.RecordedAt, indent: 16);
            WriteString(builder, "honua:workPackageId", observation.WorkPackageId, indent: 16);
            WriteDouble(builder, "honua:longitude", observation.Longitude, indent: 16);
            WriteDouble(builder, "honua:latitude", observation.Latitude, indent: 16);
            WriteDouble(builder, "honua:elevation", observation.Elevation, indent: 16);
            WriteString(builder, "honua:evidenceKind", observation.EvidenceKind, indent: 16);
            WriteInt(builder, "honua:evidenceCount", observation.EvidenceCount, indent: 16);
            builder.AppendLine("            }");
            builder.AppendLine("        )");
            builder.AppendLine("        {");
            builder.AppendLine("        }");
        }

        builder.AppendLine("    }");
        builder.AppendLine();
    }

    private static void WriteString(StringBuilder builder, string key, string? value, int indent)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        builder.Append(' ', indent);
        builder.Append("string ");
        builder.Append(key);
        builder.Append(" = ");
        builder.AppendLine(UsdString(value));
    }

    private static void WriteStringArray(StringBuilder builder, string key, IEnumerable<string?>? values, int indent)
    {
        var filtered = values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
        if (filtered is not { Length: > 0 })
        {
            return;
        }

        builder.Append(' ', indent);
        builder.Append("string[] ");
        builder.Append(key);
        builder.Append(" = [");
        builder.Append(string.Join(", ", filtered.Select(UsdString)));
        builder.AppendLine("]");
    }

    private static void WriteDouble(StringBuilder builder, string key, double? value, int indent)
    {
        if (!value.HasValue || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return;
        }

        builder.Append(' ', indent);
        builder.Append("double ");
        builder.Append(key);
        builder.Append(" = ");
        builder.AppendLine(value.Value.ToString("G17", CultureInfo.InvariantCulture));
    }

    private static void WriteInt(StringBuilder builder, string key, int value, int indent)
    {
        builder.Append(' ', indent);
        builder.Append("int ");
        builder.Append(key);
        builder.Append(" = ");
        builder.AppendLine(value.ToString(CultureInfo.InvariantCulture));
    }

    private static string UsdString(string value)
    {
        var builder = new StringBuilder(value.Length + 2);
        builder.Append('"');
        foreach (var c in value)
        {
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(c) => "\\u" + ((int)c).ToString("x4", CultureInfo.InvariantCulture),
                _ => c.ToString()
            });
        }

        builder.Append('"');
        return builder.ToString();
    }

    private static string ToUsdIdentifier(string value)
    {
        var builder = new StringBuilder(value.Length + 1);
        foreach (var c in value)
        {
            builder.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
        }

        if (builder.Length == 0 || char.IsDigit(builder[0]))
        {
            builder.Insert(0, '_');
        }

        return builder.ToString();
    }

    private static string ComputeETag(byte[] bytes)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(bytes);
        return $"\"{Convert.ToHexString(hash)}\"";
    }
}

internal sealed class OpenUsdTilesetManifestInput
{
    [JsonPropertyName("asset")]
    public OpenUsdTilesetAsset? Asset { get; set; }

    [JsonPropertyName("extras")]
    public OpenUsdTilesetExtras? Extras { get; set; }

    [JsonPropertyName("root")]
    public OpenUsdTilesetRoot? Root { get; set; }
}

internal sealed class OpenUsdTilesetAsset
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("tilesetVersion")]
    public string? TilesetVersion { get; set; }

    [JsonPropertyName("generator")]
    public string? Generator { get; set; }
}

internal sealed class OpenUsdTilesetExtras
{
    [JsonPropertyName("attribution")]
    public string? Attribution { get; set; }

    [JsonPropertyName("layerKind")]
    public string? LayerKind { get; set; }

    [JsonPropertyName("layerId")]
    public string? LayerId { get; set; }

    [JsonPropertyName("bounds")]
    public OpenUsdBounds? Bounds { get; set; }

    [JsonPropertyName("projectMeta")]
    public OpenUsdProjectMeta? ProjectMeta { get; set; }

    [JsonPropertyName("observationsLayer")]
    public OpenUsdObservationsLayer? ObservationsLayer { get; set; }

    [JsonPropertyName("observationsSidecar")]
    public OpenUsdObservationsSidecar? ObservationsSidecar { get; set; }
}

internal sealed class OpenUsdBounds
{
    [JsonPropertyName("west")]
    public double West { get; set; }

    [JsonPropertyName("south")]
    public double South { get; set; }

    [JsonPropertyName("east")]
    public double East { get; set; }

    [JsonPropertyName("north")]
    public double North { get; set; }

    [JsonPropertyName("minHeight")]
    public double? MinHeight { get; set; }

    [JsonPropertyName("maxHeight")]
    public double? MaxHeight { get; set; }
}

internal sealed class OpenUsdProjectMeta
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("phase")]
    public string? Phase { get; set; }

    [JsonPropertyName("completionRatio")]
    public double? CompletionRatio { get; set; }

    [JsonPropertyName("startDate")]
    public string? StartDate { get; set; }

    [JsonPropertyName("expectedEndDate")]
    public string? ExpectedEndDate { get; set; }

    [JsonPropertyName("stakeholders")]
    public List<OpenUsdStakeholder>? Stakeholders { get; set; }

    [JsonPropertyName("workPackages")]
    public List<OpenUsdWorkPackage>? WorkPackages { get; set; }

    [JsonPropertyName("parcels")]
    public List<OpenUsdParcel>? Parcels { get; set; }
}

internal sealed class OpenUsdStakeholder
{
    [JsonPropertyName("role")]
    public string? Role { get; set; }
}

internal sealed class OpenUsdWorkPackage
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class OpenUsdParcel
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

internal sealed class OpenUsdObservationsLayer
{
    [JsonPropertyName("sidecarUri")]
    public string? SidecarUri { get; set; }

    [JsonPropertyName("tilesetSceneId")]
    public string? TilesetSceneId { get; set; }
}

internal sealed class OpenUsdObservationsSidecar
{
    [JsonPropertyName("uri")]
    public string? Uri { get; set; }

    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }
}

internal sealed class OpenUsdTilesetRoot
{
    [JsonPropertyName("boundingVolume")]
    public OpenUsdBoundingVolume? BoundingVolume { get; set; }
}

internal sealed class OpenUsdBoundingVolume
{
    [JsonPropertyName("region")]
    public List<double>? Region { get; set; }
}

internal sealed class OpenUsdObservationsManifestInput
{
    [JsonPropertyName("schemaVersion")]
    public string? SchemaVersion { get; set; }

    [JsonPropertyName("sceneId")]
    public string? SceneId { get; set; }

    [JsonPropertyName("projectId")]
    public string? ProjectId { get; set; }

    [JsonPropertyName("generatedAt")]
    public string? GeneratedAt { get; set; }

    [JsonPropertyName("observations")]
    public List<OpenUsdObservation>? Observations { get; set; }

    [JsonIgnore]
    public string? SidecarUri { get; set; }

    [JsonIgnore]
    public string? SidecarUrl { get; set; }
}

internal sealed class OpenUsdObservation
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("kind")]
    public string? Kind { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("severity")]
    public string? Severity { get; set; }

    [JsonPropertyName("longitude")]
    public double Longitude { get; set; }

    [JsonPropertyName("latitude")]
    public double Latitude { get; set; }

    [JsonPropertyName("elevation")]
    public double? Elevation { get; set; }

    [JsonPropertyName("recordedAt")]
    public string? RecordedAt { get; set; }

    [JsonPropertyName("evidenceKind")]
    public string? EvidenceKind { get; set; }

    [JsonPropertyName("evidenceCount")]
    public int EvidenceCount { get; set; }

    [JsonPropertyName("workPackageId")]
    public string? WorkPackageId { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OpenUsdTilesetManifestInput))]
[JsonSerializable(typeof(OpenUsdTilesetAsset))]
[JsonSerializable(typeof(OpenUsdTilesetExtras))]
[JsonSerializable(typeof(OpenUsdBounds))]
[JsonSerializable(typeof(OpenUsdProjectMeta))]
[JsonSerializable(typeof(OpenUsdStakeholder))]
[JsonSerializable(typeof(OpenUsdWorkPackage))]
[JsonSerializable(typeof(OpenUsdParcel))]
[JsonSerializable(typeof(OpenUsdObservationsLayer))]
[JsonSerializable(typeof(OpenUsdObservationsSidecar))]
[JsonSerializable(typeof(OpenUsdTilesetRoot))]
[JsonSerializable(typeof(OpenUsdBoundingVolume))]
[JsonSerializable(typeof(OpenUsdObservationsManifestInput))]
[JsonSerializable(typeof(OpenUsdObservation))]
internal sealed partial class OpenUsdSceneJsonContext : JsonSerializerContext
{
}
