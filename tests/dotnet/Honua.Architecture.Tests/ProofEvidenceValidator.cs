// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Architecture.Tests;

/// <summary>
/// The kind of substantiation an <c>evidenceLocations</c> entry in the public-interface proof
/// ledger provides. The classification is what lets the ledger tell a gate an automated lane runs
/// from a document that merely happens to exist (honua-server#2877).
/// </summary>
internal enum EvidenceKind
{
    /// <summary>Path does not exist on disk.</summary>
    Missing,

    /// <summary>A test under <c>tests/dotnet/Honua.Architecture.Tests/</c> — the architecture gate.</summary>
    ArchitectureTest,

    /// <summary>A <c>*Tests.cs</c> file under <c>tests/dotnet/</c> — runs in the dotnet test lanes.</summary>
    DotnetTest,

    /// <summary>A python/js/js-browser test file — runs in an integration or browser lane.</summary>
    ForeignTest,

    /// <summary>A <c>.github/workflows/*.yml</c> governance or conformance workflow.</summary>
    Workflow,

    /// <summary>
    /// A source-generated <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>: a
    /// build-enforced wire contract. The warnings-as-errors Release build fails if the context and
    /// its model diverge, so the shape is gated at compile time in the build/test lanes.
    /// </summary>
    SourceGenContract,

    /// <summary>The published gRPC proto schema on <c>github.com/honua-io/geospatial-grpc</c>.</summary>
    GrpcProtoUrl,

    /// <summary>Non-gate source (compiles, but does not by itself gate the claim).</summary>
    Source,

    /// <summary>Prose documentation (<c>*.md</c>). Corroborating, never a gate.</summary>
    Doc,

    /// <summary>A published data artifact (<c>*.json</c>, cert envelopes). Corroborating, never a gate.</summary>
    Data,

    /// <summary>An external artifact URL that is not the gRPC proto schema.</summary>
    ExternalUrl,

    /// <summary>Anything else — support/config files under a test tree, scripts, etc.</summary>
    Other,
}

/// <summary>
/// Validates that a proof's evidence actually substantiates its claim rather than merely existing.
/// Extracted so the guarantee can be exercised directly with synthetic proofs (see
/// <see cref="PublicInterfaceProofLedgerTests"/>) as well as swept over the committed ledger.
/// </summary>
internal static class ProofEvidenceValidator
{
    /// <summary>
    /// Returns the human-readable reasons <paramref name="proof"/> fails content validation, or an
    /// empty list when the proof is substantiated. <paramref name="surface"/> supplies sibling
    /// proofs so a manual/review-lane proof can be checked for a real automated gate elsewhere on
    /// the surface.
    /// </summary>
    public static IReadOnlyList<string> Validate(
        PublicInterfaceSurface surface,
        PublicInterfaceProof proof,
        string repoRoot)
    {
        var failures = new List<string>();
        var classified = proof.EvidenceLocations
            .Select(location => (Location: location, Kind: Classify(location, repoRoot)))
            .ToArray();

        // Existence is necessary (but not sufficient). Preserved from the original File.Exists check.
        foreach (var (location, kind) in classified)
        {
            if (kind == EvidenceKind.Missing)
            {
                failures.Add(
                    $"evidence location '{location}' for surface '{surface.SurfaceId}' " +
                    $"({proof.ProofClass}) must exist");
            }
        }

        // Planned / bounded-child proofs are honestly unfinished (a follow-up ticket is required
        // elsewhere); they are not asserting a live gate, so the gate requirement does not apply.
        if (!string.Equals(proof.Status, "implemented", StringComparison.OrdinalIgnoreCase))
        {
            return failures;
        }

        var laneTokens = SplitLane(proof.ExecutionLane);
        var automatedTokens = laneTokens.Where(IsAutomatedLaneToken).ToArray();

        if (automatedTokens.Length > 0)
        {
            if (!IsGateSatisfied(proof, classified, automatedTokens))
            {
                var citedKinds = string.Join(", ", classified.Select(entry => $"{entry.Location}={entry.Kind}"));
                failures.Add(
                    $"proof '{proof.ProofClass}' on surface '{surface.SurfaceId}' declares automated " +
                    $"execution lane '{proof.ExecutionLane}' but cites no executable gate that lane " +
                    $"runs — a document or model that merely exists is not proof (evidence: {citedKinds})");
            }
        }
        else
        {
            // Purely manual/review lane: admissible only as corroboration of a surface that is
            // gated for real elsewhere. Manual evidence can never be the sole substantiation.
            var hasGatedSibling = surface.Proofs.Any(sibling =>
                !ReferenceEquals(sibling, proof) &&
                string.Equals(sibling.Status, "implemented", StringComparison.OrdinalIgnoreCase) &&
                HasAutomatedGate(sibling, repoRoot));

            if (!hasGatedSibling)
            {
                failures.Add(
                    $"proof '{proof.ProofClass}' on surface '{surface.SurfaceId}' runs only on the " +
                    $"manual lane '{proof.ExecutionLane}' and the surface carries no automated-gate " +
                    "proof — manual/review evidence cannot be the sole substantiation of a surface");
            }
        }

        return failures;
    }

    /// <summary>
    /// True when <paramref name="proof"/> is implemented, declares an automated lane, and cites a
    /// gate that lane runs. Used to decide whether a manual sibling proof is genuinely corroborated.
    /// </summary>
    private static bool HasAutomatedGate(PublicInterfaceProof proof, string repoRoot)
    {
        var automatedTokens = SplitLane(proof.ExecutionLane).Where(IsAutomatedLaneToken).ToArray();
        if (automatedTokens.Length == 0)
        {
            return false;
        }

        var classified = proof.EvidenceLocations
            .Select(location => (Location: location, Kind: Classify(location, repoRoot)))
            .ToArray();

        return IsGateSatisfied(proof, classified, automatedTokens);
    }

    private static bool IsGateSatisfied(
        PublicInterfaceProof proof,
        (string Location, EvidenceKind Kind)[] classified,
        string[] automatedTokens) =>
        automatedTokens.Any(token =>
            classified.Any(entry => IsGateCompatible(token, entry.Kind, proof.ProofClass, entry.Location)));

    /// <summary>
    /// True when an evidence location of the given <paramref name="kind"/> is a gate the automated
    /// lane <paramref name="laneToken"/> actually runs.
    /// </summary>
    private static bool IsGateCompatible(string laneToken, EvidenceKind kind, string proofClass, string location)
    {
        // Order matters: "ci:test-all+architecture" splits into "ci:test-all" and "architecture";
        // the "architecture" / "test-all" checks must win over the generic "ci:" fallback.
        if (laneToken.Contains("architecture", StringComparison.OrdinalIgnoreCase))
        {
            return kind == EvidenceKind.ArchitectureTest;
        }

        if (laneToken.Contains("test-all", StringComparison.OrdinalIgnoreCase))
        {
            // The test-all lane (ci.yml) runs the whole dotnet + python + js suite, so any of those
            // test kinds is a gate it executes. For contract-governance, a source-generated wire
            // contract additionally counts: the warnings-as-errors Release build gates its shape.
            return kind is EvidenceKind.DotnetTest or EvidenceKind.ArchitectureTest or EvidenceKind.ForeignTest
                || (kind == EvidenceKind.SourceGenContract
                    && string.Equals(proofClass, "contract-governance", StringComparison.OrdinalIgnoreCase));
        }

        if (laneToken.StartsWith("nightly:", StringComparison.OrdinalIgnoreCase))
        {
            return kind == EvidenceKind.Workflow
                && WorkflowMatchesLane(location, laneToken["nightly:".Length..]);
        }

        if (laneToken.StartsWith("repo:honua-io/geospatial-grpc", StringComparison.OrdinalIgnoreCase))
        {
            return kind == EvidenceKind.GrpcProtoUrl;
        }

        if (laneToken.StartsWith("ci:", StringComparison.OrdinalIgnoreCase))
        {
            // Broad CI lanes run the whole test + workflow suite; any executed test or workflow gate
            // is a valid gate for a ci: lane.
            return kind is EvidenceKind.Workflow
                or EvidenceKind.DotnetTest
                or EvidenceKind.ForeignTest
                or EvidenceKind.ArchitectureTest;
        }

        return false;
    }

    /// <summary>
    /// True when the cited workflow file's name corresponds to the nightly lane it is claimed to run
    /// in. Prevents a nightly proof from citing an unrelated workflow (the "workflow that answers an
    /// unrelated question" failure).
    /// </summary>
    private static bool WorkflowMatchesLane(string workflowPath, string laneCore)
    {
        var stem = Path.GetFileNameWithoutExtension(workflowPath);
        return stem.Contains(laneCore, StringComparison.OrdinalIgnoreCase)
            || laneCore.Contains(stem, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAutomatedLaneToken(string token) =>
        token.Contains("architecture", StringComparison.OrdinalIgnoreCase)
        || token.Contains("test-all", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("ci:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("nightly:", StringComparison.OrdinalIgnoreCase)
        || token.StartsWith("repo:", StringComparison.OrdinalIgnoreCase);

    private static string[] SplitLane(string executionLane) =>
        executionLane.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Classifies one evidence location by kind, reading file content only where necessary to
    /// distinguish a build-enforced source-generated contract from ordinary source.
    /// </summary>
    public static EvidenceKind Classify(string evidenceLocation, string repoRoot)
    {
        if (Uri.TryCreate(evidenceLocation, UriKind.Absolute, out var uri)
            && (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)))
        {
            return string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)
                && uri.AbsolutePath.StartsWith("/honua-io/geospatial-grpc", StringComparison.OrdinalIgnoreCase)
                    ? EvidenceKind.GrpcProtoUrl
                    : EvidenceKind.ExternalUrl;
        }

        var normalized = evidenceLocation.Replace('\\', '/');

        if (!File.Exists(ArchitectureTestHelpers.CombinePath(repoRoot, evidenceLocation)))
        {
            return EvidenceKind.Missing;
        }

        if (normalized.StartsWith("tests/dotnet/Honua.Architecture.Tests/", StringComparison.OrdinalIgnoreCase)
            && normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceKind.ArchitectureTest;
        }

        if (normalized.StartsWith("tests/dotnet/", StringComparison.OrdinalIgnoreCase)
            && normalized.EndsWith("Tests.cs", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceKind.DotnetTest;
        }

        if (IsForeignTest(normalized))
        {
            return EvidenceKind.ForeignTest;
        }

        if (normalized.StartsWith(".github/workflows/", StringComparison.OrdinalIgnoreCase)
            && (normalized.EndsWith(".yml", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)))
        {
            return EvidenceKind.Workflow;
        }

        if (normalized.StartsWith("src/", StringComparison.OrdinalIgnoreCase)
            && normalized.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        {
            return IsSourceGeneratedContract(ArchitectureTestHelpers.CombinePath(repoRoot, evidenceLocation))
                ? EvidenceKind.SourceGenContract
                : EvidenceKind.Source;
        }

        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceKind.Doc;
        }

        if (normalized.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            return EvidenceKind.Data;
        }

        return EvidenceKind.Other;
    }

    private static bool IsForeignTest(string normalizedPath)
    {
        if (normalizedPath.StartsWith("tests/python/", StringComparison.OrdinalIgnoreCase)
            && normalizedPath.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(normalizedPath);
            return name.StartsWith("test_", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("_test.py", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, "conftest.py", StringComparison.OrdinalIgnoreCase);
        }

        if ((normalizedPath.StartsWith("tests/js/", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith("tests/js-browser/", StringComparison.OrdinalIgnoreCase))
            && (normalizedPath.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase)
                || normalizedPath.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool IsSourceGeneratedContract(string absolutePath)
    {
        try
        {
            var text = File.ReadAllText(absolutePath);
            return text.Contains("JsonSerializerContext", StringComparison.Ordinal)
                || text.Contains("[JsonSerializable", StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
