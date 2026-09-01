// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;

namespace Honua.TestKit;

/// <summary>
/// Resolves immutable, digest-verified assets from the shared curated test corpus.
/// </summary>
public sealed class CuratedCorpus
{
    private const string SupportedSchemaVersion = "honua.curated-test-corpus/v1";
    private readonly string _root;
    private readonly IReadOnlyDictionary<string, CuratedCorpusAsset> _assets;

    private CuratedCorpus(string root, string revision, IReadOnlyDictionary<string, CuratedCorpusAsset> assets)
    {
        _root = root;
        Revision = revision;
        _assets = assets;
    }

    /// <summary>Gets the immutable corpus revision.</summary>
    public string Revision { get; }

    /// <summary>Gets all declared assets in stable identifier order.</summary>
    public IReadOnlyCollection<CuratedCorpusAsset> Assets => _assets.Values.ToArray();

    /// <summary>Loads a committed corpus revision from the repository fixture root.</summary>
    public static CuratedCorpus Load(string revision = "v1")
    {
        ValidateRevision(revision);
        return LoadFromDirectory(RepositoryPaths.Resolve("tests", "fixtures", "curated-edge-corpus", revision));
    }

    /// <summary>Loads a corpus rooted at an explicit directory, primarily for integrity tests.</summary>
    public static CuratedCorpus LoadFromDirectory(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var fullRoot = Path.GetFullPath(root);
        var manifestPath = Path.Join(fullRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            throw new InvalidDataException($"Curated corpus manifest is missing: {manifestPath}");
        }

        using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
        var document = manifest.RootElement;
        RequireString(document, "schemaVersion", SupportedSchemaVersion);
        var identity = RequireObject(document, "identity");
        RequireString(identity, "id", "curated-edge-corpus");
        var revision = RequireString(identity, "revision");
        ValidateRevision(revision);

        var assetElements = RequireArray(document, "assets");
        var assets = new SortedDictionary<string, CuratedCorpusAsset>(StringComparer.Ordinal);
        foreach (var element in assetElements.EnumerateArray())
        {
            var asset = ParseAsset(element);
            if (!assets.TryAdd(asset.Id, asset))
            {
                throw new InvalidDataException($"Curated corpus asset ID is duplicated: {asset.Id}");
            }
        }

        if (assets.Count == 0)
        {
            throw new InvalidDataException("Curated corpus manifest contains no assets.");
        }

        return new CuratedCorpus(fullRoot, revision, assets);
    }

    /// <summary>Verifies and returns an asset's absolute filesystem path.</summary>
    public string ResolveVerifiedPath(string assetId)
    {
        var asset = GetAsset(assetId);
        var path = ResolveContainedPath(asset.Path);
        if (!File.Exists(path))
        {
            throw new InvalidDataException($"Curated corpus asset is missing: {asset.Id}");
        }

        using var stream = File.OpenRead(path);
        if (stream.Length != asset.ByteLength)
        {
            throw new InvalidDataException(
                $"Curated corpus asset length mismatch for {asset.Id}: expected {asset.ByteLength}, actual {stream.Length}.");
        }

        var actualDigest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(asset.Sha256),
                Convert.FromHexString(actualDigest)))
        {
            throw new InvalidDataException($"Curated corpus asset digest mismatch: {asset.Id}");
        }

        return path;
    }

    /// <summary>Verifies and reads an asset into memory.</summary>
    public byte[] ReadAllBytes(string assetId) => File.ReadAllBytes(ResolveVerifiedPath(assetId));

    /// <summary>Verifies every asset declared by the manifest.</summary>
    public void VerifyAll()
    {
        foreach (var asset in Assets)
        {
            ResolveVerifiedPath(asset.Id);
        }
    }

    private CuratedCorpusAsset GetAsset(string assetId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetId);
        return _assets.TryGetValue(assetId, out var asset)
            ? asset
            : throw new KeyNotFoundException($"Unknown curated corpus asset: {assetId}");
    }

    private string ResolveContainedPath(string relativePath)
    {
        if (Path.IsPathRooted(relativePath) || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Curated corpus asset path must be portable and relative: {relativePath}");
        }

        var segments = relativePath.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".."))
        {
            throw new InvalidDataException($"Curated corpus asset path contains an unsafe segment: {relativePath}");
        }

        var path = Path.GetFullPath(Path.Join(_root, relativePath));
        var rootPrefix = _root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!path.StartsWith(rootPrefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Curated corpus asset path escapes its revision root: {relativePath}");
        }

        return path;
    }

    private static CuratedCorpusAsset ParseAsset(JsonElement element)
    {
        var id = RequireString(element, "id");
        if (id.Length > 80 || id.Any(character => !(char.IsAsciiLetterOrDigit(character) || character is '-' or '_')))
        {
            throw new InvalidDataException($"Curated corpus asset ID is invalid: {id}");
        }

        var path = RequireString(element, "path");
        var mediaType = RequireString(element, "mediaType");
        var byteLength = element.TryGetProperty("byteLength", out var lengthElement) && lengthElement.TryGetInt64(out var length)
            ? length
            : throw new InvalidDataException($"Curated corpus asset {id} has no valid byteLength.");
        if (byteLength < 0)
        {
            throw new InvalidDataException($"Curated corpus asset {id} has a negative byteLength.");
        }

        var digest = RequireString(element, "sha256");
        if (digest.Length != 64 || digest.Any(character => !char.IsAsciiHexDigit(character) || char.IsUpper(character)))
        {
            throw new InvalidDataException($"Curated corpus asset {id} has an invalid SHA-256 digest.");
        }

        var facets = RequireArray(element, "facets").EnumerateArray().Select(value => value.GetString()
            ?? throw new InvalidDataException($"Curated corpus asset {id} has a non-string facet.")).ToArray();
        if (facets.Length == 0 || facets.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException($"Curated corpus asset {id} must declare scenario facets.");
        }

        return new CuratedCorpusAsset(id, path, mediaType, byteLength, digest, facets);
    }

    private static JsonElement RequireObject(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Object
            ? value
            : throw new InvalidDataException($"Curated corpus manifest requires object '{propertyName}'.");

    private static JsonElement RequireArray(JsonElement parent, string propertyName)
        => parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Array
            ? value
            : throw new InvalidDataException($"Curated corpus manifest requires array '{propertyName}'.");

    private static string RequireString(JsonElement parent, string propertyName, string? expected = null)
    {
        var value = parent.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(value) || (expected is not null && !string.Equals(value, expected, StringComparison.Ordinal)))
        {
            throw new InvalidDataException($"Curated corpus manifest has invalid '{propertyName}'.");
        }

        return value;
    }

    private static void ValidateRevision(string revision)
    {
        if (revision.Length is < 2 or > 16 || revision[0] != 'v' || revision[1..].Any(character => !char.IsAsciiDigit(character)))
        {
            throw new InvalidDataException($"Curated corpus revision is invalid: {revision}");
        }
    }
}

/// <summary>Immutable metadata for one digest-bound corpus asset.</summary>
public sealed record CuratedCorpusAsset(
    string Id,
    string Path,
    string MediaType,
    long ByteLength,
    string Sha256,
    IReadOnlyList<string> Facets);
