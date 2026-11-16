// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Geoprocessing.Testing;

public static class GoldenFixtureLoader
{
    public const string ManifestFileName = "fixture.json";

    private static readonly JsonSerializerOptions ManifestOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static IReadOnlyList<GoldenFixture> Discover(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Directory.Exists(rootDirectory))
        {
            throw new DirectoryNotFoundException($"GP fixture root not found: {rootDirectory}");
        }

        var fixtures = new List<GoldenFixture>();
        foreach (var manifestPath in Directory.EnumerateFiles(rootDirectory, ManifestFileName, SearchOption.AllDirectories))
        {
            fixtures.Add(Load(manifestPath));
        }

        return fixtures.OrderBy(f => f.Id, StringComparer.Ordinal).ToArray();
    }

    public static GoldenFixture Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException($"GP fixture manifest not found: {manifestPath}", manifestPath);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(manifestPath))!;
        var json = File.ReadAllText(manifestPath);
        var manifest = JsonSerializer.Deserialize<GoldenFixtureManifest>(json, ManifestOptions)
            ?? throw new InvalidDataException($"GP fixture manifest is empty or invalid: {manifestPath}");

        if (string.IsNullOrWhiteSpace(manifest.Id))
        {
            throw new InvalidDataException($"GP fixture manifest is missing 'id': {manifestPath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Process))
        {
            throw new InvalidDataException($"GP fixture manifest is missing 'process': {manifestPath}");
        }

        if (string.IsNullOrWhiteSpace(manifest.Golden))
        {
            throw new InvalidDataException($"GP fixture manifest is missing 'golden': {manifestPath}");
        }

        if (Path.IsPathRooted(manifest.Golden))
        {
            throw new InvalidDataException(
                $"GP fixture '{manifest.Id}' declares an absolute 'golden' path "
                + $"'{manifest.Golden}'; golden must be relative to the manifest's directory.");
        }

        var inputs = new Dictionary<string, string>(StringComparer.Ordinal);
        if (manifest.Inputs is not null)
        {
            foreach (var (key, value) in manifest.Inputs)
            {
                inputs[key] = value ?? string.Empty;
            }
        }

        if (manifest.InputFiles is not null)
        {
            foreach (var (key, relativePath) in manifest.InputFiles)
            {
                if (Path.IsPathRooted(relativePath))
                {
                    throw new InvalidDataException(
                        $"GP fixture '{manifest.Id}' declares an absolute inputFiles path "
                        + $"'{relativePath}'; inputFiles must be relative to the manifest's directory.");
                }

                // relativePath was validated as non-rooted immediately above, so Path.Combine
                // cannot silently drop `directory` here.
                var path = Path.Join(directory, relativePath);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException(
                        $"GP fixture '{manifest.Id}' references missing input file '{relativePath}'.", path);
                }

                inputs[key] = Convert.ToBase64String(File.ReadAllBytes(path));
            }
        }

        var tolerance = manifest.Tolerance is null
            ? GoldenTolerance.Default
            : GoldenTolerance.Create(
                manifest.Tolerance.Coordinate ?? GoldenTolerance.Default.Coordinate,
                manifest.Tolerance.Numeric ?? GoldenTolerance.Default.Numeric);

        // manifest.Golden was validated as non-rooted above, so Path.Combine cannot silently
        // drop `directory` here.
        return new GoldenFixture(
            manifest.Id,
            manifest.Process,
            inputs,
            Path.Join(directory, manifest.Golden),
            ParseMode(manifest.Mode),
            tolerance,
            manifest.Description);
    }

    private static GoldenComparisonMode ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        null or "" or "auto" => GoldenComparisonMode.Auto,
        "geometry" => GoldenComparisonMode.Geometry,
        "scalarstructural" or "scalar" or "structural" => GoldenComparisonMode.ScalarStructural,
        _ => throw new InvalidDataException(
            $"Unknown GP fixture comparison mode '{mode}'. Expected 'auto', 'geometry', or 'scalarStructural'."),
    };
}

internal sealed class GoldenFixtureManifest
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("process")]
    public string? Process { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("mode")]
    public string? Mode { get; set; }

    [JsonPropertyName("tolerance")]
    public GoldenToleranceManifest? Tolerance { get; set; }

    [JsonPropertyName("inputs")]
    public Dictionary<string, string?>? Inputs { get; set; }

    [JsonPropertyName("inputFiles")]
    public Dictionary<string, string>? InputFiles { get; set; }

    [JsonPropertyName("golden")]
    public string? Golden { get; set; }
}

internal sealed class GoldenToleranceManifest
{
    [JsonPropertyName("coordinate")]
    public double? Coordinate { get; set; }

    [JsonPropertyName("numeric")]
    public double? Numeric { get; set; }
}
