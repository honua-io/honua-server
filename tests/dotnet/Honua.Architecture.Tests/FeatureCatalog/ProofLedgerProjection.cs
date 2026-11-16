// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Architecture.Tests.FeatureCatalog;

internal sealed class ProofLedgerProjection
{
    private readonly IReadOnlyList<ProofLedgerSurface> _surfaces;

    private ProofLedgerProjection(IReadOnlyList<ProofLedgerSurface> surfaces) => _surfaces = surfaces;

    public static ProofLedgerProjection Load()
    {
        var repoRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var ledgerPath = Honua.Architecture.Tests.ArchitectureTestHelpers.CombinePath(repoRoot, "docs", "gis", "data", "public-interface-proof.json");

        using var stream = File.OpenRead(ledgerPath);
        var document = JsonSerializer.Deserialize(stream, ProofLedgerJsonContext.Default.ProofLedgerDocument)
            ?? throw new InvalidOperationException("Unable to deserialize public-interface-proof.json.");

        return new ProofLedgerProjection(document.Surfaces);
    }

    public ResolvedSurface? ResolveSurface(string path)
    {
        var surface = _surfaces.FirstOrDefault(candidate => Matches(candidate, path));
        if (surface is null)
        {
            return null;
        }

        var codeLocation = surface.Proofs
            .Where(proof => string.Equals(proof.ProofClass, "route-coverage", StringComparison.OrdinalIgnoreCase))
            .SelectMany(proof => proof.EvidenceLocations)
            .Concat(surface.Proofs.SelectMany(proof => proof.EvidenceLocations))
            .FirstOrDefault(location => !IsExternal(location))
            ?? "src/Honua.Server/EndpointRegistry.cs";

        return new ResolvedSurface(surface.SurfaceId, surface.Protocol, surface.SurfaceKind, codeLocation);
    }

    private static bool Matches(ProofLedgerSurface surface, string path)
        => surface.EndpointExactMatches.Contains(path, StringComparer.OrdinalIgnoreCase)
        || surface.EndpointPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        || surface.EndpointContains.Any(value => path.Contains(value, StringComparison.OrdinalIgnoreCase));

    private static bool IsExternal(string location)
        => Uri.TryCreate(location, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    internal sealed record ResolvedSurface(string SurfaceId, string Protocol, string SurfaceKind, string CodeLocation);
}

internal sealed class ProofLedgerDocument
{
    public ProofLedgerSurface[] Surfaces { get; init; } = [];
}

internal sealed class ProofLedgerSurface
{
    public string SurfaceId { get; init; } = string.Empty;

    public string SurfaceKind { get; init; } = string.Empty;

    public string Protocol { get; init; } = string.Empty;

    public string[] EndpointPrefixes { get; init; } = [];

    public string[] EndpointExactMatches { get; init; } = [];

    public string[] EndpointContains { get; init; } = [];

    public ProofLedgerProof[] Proofs { get; init; } = [];
}

internal sealed class ProofLedgerProof
{
    public string ProofClass { get; init; } = string.Empty;

    public string[] EvidenceLocations { get; init; } = [];
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ProofLedgerDocument))]
internal sealed partial class ProofLedgerJsonContext : JsonSerializerContext
{
}
