// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Architecture.Tests.FeatureCatalog;

/// <summary>
/// Read-only projection of <c>docs/gis/data/public-interface-proof.json</c> used
/// by the feature-catalog generator. It mirrors the endpoint→surface matching
/// semantics enforced by
/// <c>PublicInterfaceProofLedgerTests.EveryEndpointRegistryRoute_ShouldBeCoveredByProofLedgerSurface</c>
/// so the catalog's <c>proof_ledger_surface</c> and <c>code_location</c> columns
/// stay consistent with the proof ledger that CI already gates.
/// </summary>
internal sealed class ProofLedgerProjection
{
    private const string DefaultRegistryLocation = "src/Honua.Server/EndpointRegistry.cs";
    private readonly IReadOnlyList<ProofLedgerSurface> _surfaces;

    private ProofLedgerProjection(IReadOnlyList<ProofLedgerSurface> surfaces)
    {
        _surfaces = surfaces;
        ValidateImplementationMappings();
    }

    /// <summary>Loads and projects the committed proof ledger.</summary>
    public static ProofLedgerProjection Load()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var ledgerPath = Honua.Architecture.Tests.ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "public-interface-proof.json");

        using var stream = File.OpenRead(ledgerPath);
        var document = JsonSerializer.Deserialize(stream, ProofLedgerJsonContext.Default.ProofLedgerDocument)
            ?? throw new InvalidOperationException("Unable to deserialize public-interface-proof.json.");

        return new ProofLedgerProjection(document.Surfaces);
    }

    /// <summary>
    /// Resolves the first proof-ledger surface that claims the supplied route,
    /// matching by exact path, prefix, or contains — the same selectors the proof
    /// ledger declares. Implementation endpoint/handler evidence is preferred for
    /// <c>code_location</c>; surfaces without it retain their registry fallback.
    /// Returns <c>null</c> when no surface matches (which the existing ledger
    /// governance test already forbids for shipped routes).
    /// </summary>
    public ResolvedSurface? ResolveSurface(string method, string path)
    {
        var surface = _surfaces.FirstOrDefault(candidate => Matches(candidate, path));
        if (surface is null)
        {
            return null;
        }

        var routeEvidence = surface.Proofs
            .Where(proof => string.Equals(proof.ProofClass, "route-coverage", StringComparison.OrdinalIgnoreCase))
            .SelectMany(proof => proof.EvidenceLocations)
            .Where(location => !IsExternal(location))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var localEvidence = routeEvidence
            .Concat(surface.Proofs.SelectMany(proof => proof.EvidenceLocations))
            .Where(location => !IsExternal(location))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var mappedLocation = ResolveMappedImplementation(surface, method, path);
        var inferredLocation = (surface.EndpointImplementationMappings?.Length ?? 0) == 0
            ? FindSingleImplementation(routeEvidence) ?? FindSingleImplementation(localEvidence)
            : null;
        var codeLocation = mappedLocation
            ?? inferredLocation
            ?? localEvidence.FirstOrDefault(IsRegistryEntryPoint)
            ?? DefaultRegistryLocation;

        return new ResolvedSurface(surface.SurfaceId, surface.Protocol, surface.SurfaceKind, codeLocation);
    }

    private static string? ResolveMappedImplementation(
        ProofLedgerSurface surface,
        string method,
        string path)
    {
        var endpointKey = EndpointKey.Format(method, path);
        return (surface.EndpointImplementationMappings ?? [])
            .FirstOrDefault(mapping => mapping.Endpoints
                .Select(EndpointKey.Normalize)
                .Any(candidate => string.Equals(candidate, endpointKey, StringComparison.OrdinalIgnoreCase)))
            ?.CodeLocation;
    }

    private static string? FindSingleImplementation(IEnumerable<string> evidenceLocations)
    {
        var candidates = evidenceLocations
            .Where(IsImplementationEntryPoint)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToArray();

        return candidates.Length == 1 ? candidates[0] : null;
    }

    private void ValidateImplementationMappings()
    {
        foreach (var surface in _surfaces)
        {
            var localEvidence = surface.Proofs
                .SelectMany(proof => proof.EvidenceLocations)
                .Where(location => !IsExternal(location))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var mappedEndpoints = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var mapping in surface.EndpointImplementationMappings ?? [])
            {
                if (!IsImplementationEntryPoint(mapping.CodeLocation))
                {
                    throw new InvalidOperationException(
                        $"Surface '{surface.SurfaceId}' maps endpoints to non-implementation location "
                        + $"'{mapping.CodeLocation}'.");
                }

                if (!localEvidence.Contains(mapping.CodeLocation))
                {
                    throw new InvalidOperationException(
                        $"Surface '{surface.SurfaceId}' maps endpoints to '{mapping.CodeLocation}', "
                        + "but that location is not cited by the surface's proof evidence.");
                }

                var endpoints = mapping.Endpoints ?? [];
                if (endpoints.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Surface '{surface.SurfaceId}' has an implementation mapping with no endpoints.");
                }

                foreach (var endpoint in endpoints)
                {
                    var normalized = EndpointKey.Normalize(endpoint)
                        ?? throw new InvalidOperationException(
                            $"Surface '{surface.SurfaceId}' has invalid endpoint mapping '{endpoint}'.");
                    var separator = normalized.IndexOf(' ');
                    var path = normalized[(separator + 1)..];

                    if (!Matches(surface, path))
                    {
                        throw new InvalidOperationException(
                            $"Surface '{surface.SurfaceId}' maps endpoint '{normalized}', "
                            + "but its route selectors do not claim that path.");
                    }

                    if (!mappedEndpoints.Add(normalized))
                    {
                        throw new InvalidOperationException(
                            $"Surface '{surface.SurfaceId}' maps endpoint '{normalized}' more than once.");
                    }
                }
            }
        }
    }

    private static bool Matches(ProofLedgerSurface surface, string path)
        => surface.EndpointExactMatches.Contains(path, StringComparer.OrdinalIgnoreCase)
        || surface.EndpointPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || surface.EndpointContains.Any(value => path.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool IsExternal(string location)
        => Uri.TryCreate(location, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    private static bool IsImplementationEntryPoint(string location)
    {
        if (!IsCSharpSource(location))
        {
            return false;
        }

        var fileName = Path.GetFileNameWithoutExtension(location);
        if (fileName.Contains("Registry", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return fileName.Contains("Endpoint", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Handler", StringComparison.OrdinalIgnoreCase)
            || fileName.Contains("Dispatcher", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegistryEntryPoint(string location)
        => IsCSharpSource(location) &&
        Path.GetFileNameWithoutExtension(location).Contains(
            "Registry",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsCSharpSource(string location)
        => location.StartsWith("src/", StringComparison.OrdinalIgnoreCase) &&
        location.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

    internal sealed record ResolvedSurface(string SurfaceId, string Protocol, string SurfaceKind, string CodeLocation);
}

/// <summary>Top-level proof-ledger document for source-generated deserialization.</summary>
internal sealed class ProofLedgerDocument
{
    /// <summary>The proof-ledger surfaces.</summary>
    public ProofLedgerSurface[] Surfaces { get; init; } = [];
}

/// <summary>A single proof-ledger surface (subset of fields the catalog needs).</summary>
internal sealed class ProofLedgerSurface
{
    /// <summary>Canonical surface id.</summary>
    public string SurfaceId { get; init; } = string.Empty;

    /// <summary>Surface kind (http-route-family, operation-surface, …).</summary>
    public string SurfaceKind { get; init; } = string.Empty;

    /// <summary>Human-readable protocol label.</summary>
    public string Protocol { get; init; } = string.Empty;

    /// <summary>Route prefixes claimed by this surface.</summary>
    public string[] EndpointPrefixes { get; init; } = [];

    /// <summary>Exact route matches claimed by this surface.</summary>
    public string[] EndpointExactMatches { get; init; } = [];

    /// <summary>Substring route matches claimed by this surface.</summary>
    public string[] EndpointContains { get; init; } = [];

    /// <summary>Proofs backing this surface.</summary>
    public ProofLedgerProof[] Proofs { get; init; } = [];

    /// <summary>
    /// Explicit method-and-route to implementation mappings for surfaces backed by
    /// more than one endpoint file.
    /// </summary>
    public ProofLedgerEndpointImplementationMapping[] EndpointImplementationMappings { get; init; } = [];
}

/// <summary>
/// Maps one or more exact endpoint keys to a source file already cited as proof evidence.
/// </summary>
internal sealed class ProofLedgerEndpointImplementationMapping
{
    /// <summary>Canonical <c>METHOD /route</c> endpoint keys.</summary>
    public string[] Endpoints { get; init; } = [];

    /// <summary>Repo-relative implementation source cited by the surface.</summary>
    public string CodeLocation { get; init; } = string.Empty;
}

/// <summary>A single proof backing a surface (subset of fields the catalog needs).</summary>
internal sealed class ProofLedgerProof
{
    /// <summary>Proof class (route-coverage, operation-coverage, …).</summary>
    public string ProofClass { get; init; } = string.Empty;

    /// <summary>Evidence locations (repo-relative paths or external URLs).</summary>
    public string[] EvidenceLocations { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProofLedgerDocument))]
internal sealed partial class ProofLedgerJsonContext : JsonSerializerContext
{
}
