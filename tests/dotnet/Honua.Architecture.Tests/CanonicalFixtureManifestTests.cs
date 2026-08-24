// Copyright 2025 Honua Authors
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Anti-drift gate for the frozen 2026.1 client-certification fixture manifest
/// (<c>docs/gis/data/client-certification-fixture.v1.json</c>,
/// honua-io/honua-server#3393).
/// </summary>
/// <remarks>
/// <para>
/// The manifest is only worth anything if it is content-addressed to the real
/// files: these tests recompute every per-file digest and every composite
/// revision from disk, so editing a seed without republishing the manifest is a
/// build failure rather than silent evidence drift.
/// </para>
/// <para>
/// The Python projection (<c>tests/python/shared/canonical_fixture.py</c>) is
/// compared symbol by symbol rather than by digest, and it is parsed — never
/// executed — so the architecture suite stays free of a Python runtime.
/// </para>
/// </remarks>
public sealed class CanonicalFixtureManifestTests
{
    private const string ManifestRelativePath = "client-certification-fixture.v1.json";
    private const string TrackingIssuePrefix = "https://github.com/honua-io/";

    private static readonly Regex DigestPattern =
        new("^sha256:[0-9a-f]{64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex ConstantAssignmentPattern =
        new(@"^(?<name>[A-Z][A-Z0-9_]*)\s*=\s*", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex IdentifierPattern =
        new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The auth profiles the 2026.1 contract requires the manifest to enumerate.</summary>
    private static readonly string[] RequiredAuthProfiles =
    [
        "anonymous", "valid-credential", "invalid-credential", "expired-credential",
        "insufficient-role-or-scope", "cross-tenant-denial", "separate-proposer-approver",
        "licensed-entitlement",
    ];

    /// <summary>The composite revisions the prose contract must publish verbatim.</summary>
    private static readonly string[] PublishedRevisions =
    [
        "fixtureRevision", "serverConfigRevision", "authPolicyRevision",
    ];

    [ArchitectureTest]
    public void FixtureManifest_PerFileDigests_MatchTheFilesOnDisk()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var manifest = ReadManifest(root);
        var inputs = manifest.RootElement.GetProperty("inputs").EnumerateArray().ToArray();

        inputs.Should().NotBeEmpty("a content-addressed manifest without inputs addresses nothing");
        inputs.Select(input => input.GetProperty("path").GetString()!)
            .Should().OnlyHaveUniqueItems("an input file is declared once");

        foreach (var input in inputs)
        {
            var relativePath = input.GetProperty("path").GetString()!;
            var declared = input.GetProperty("sha256").GetString()!;
            declared.Should().MatchRegex(DigestPattern.ToString(),
                $"{relativePath} must carry a sha256: digest");

            var absolutePath = ArchitectureTestHelpers.CombinePath(
                [root, .. relativePath.Split('/')]);
            File.Exists(absolutePath).Should().BeTrue($"manifest input {relativePath} must exist");

            ComputeFileDigest(absolutePath).Should().Be(declared,
                $"{relativePath} changed without republishing the fixture manifest");
        }

        var roles = inputs.Select(input => input.GetProperty("role").GetString()!).ToHashSet(StringComparer.Ordinal);
        roles.Should().Contain("fixture", "the fixture input set is what fixtureRevision addresses");
        roles.Should().Contain("server-config", "the server-config input set is what serverConfigRevision addresses");
    }

    [ArchitectureTest]
    public void FixtureManifest_CompositeRevisions_RecomputeFromTheDocumentedAlgorithms()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var manifest = ReadManifest(root);
        var document = manifest.RootElement;

        ComputeInputSetDigest(document, "fixture").Should().Be(
            document.GetProperty("fixtureRevision").GetString(),
            "fixtureRevision is honua.input-set-digest/v1 over every role=fixture input");

        ComputeInputSetDigest(document, "server-config").Should().Be(
            document.GetProperty("serverConfigRevision").GetString(),
            "serverConfigRevision is honua.input-set-digest/v1 over every role=server-config input");

        var canonicalAuthPolicy = CanonicalJson(document.GetProperty("authPolicy"));
        ComputeDigest(Encoding.UTF8.GetBytes(canonicalAuthPolicy)).Should().Be(
            document.GetProperty("authPolicyRevision").GetString(),
            "authPolicyRevision is honua.canonical-json-digest/v1 over the authPolicy object");

        var algorithms = document.GetProperty("digestAlgorithms");
        algorithms.GetProperty("fileDigest").GetProperty("id").GetString().Should().Be("honua.file-digest/v1");
        algorithms.GetProperty("inputSetDigest").GetProperty("id").GetString().Should().Be("honua.input-set-digest/v1");
        algorithms.GetProperty("canonicalJsonDigest").GetProperty("id").GetString().Should().Be("honua.canonical-json-digest/v1");

        // The receipt bindings the lanes actually emit must be values this manifest publishes.
        var inputDigests = document.GetProperty("inputs").EnumerateArray()
            .ToDictionary(input => input.GetProperty("path").GetString()!,
                          input => input.GetProperty("sha256").GetString()!,
                          StringComparer.Ordinal);
        var receipts = document.GetProperty("receiptBindings").GetProperty("currentValues");
        receipts.GetProperty("fixture_revision").GetProperty("value").GetString().Should()
            .Be(inputDigests["tests/seed/client-compat-v1.sql"],
                "the emitted fixture_revision must be a digest this manifest publishes");
        receipts.GetProperty("server_config_revision").GetProperty("value").GetString().Should()
            .Be(inputDigests["tests/config/client-compat-server-v1.json"],
                "the emitted server_config_revision must be a digest this manifest publishes");
        receipts.GetProperty("auth_policy_revision").GetProperty("value").GetString().Should()
            .Be(document.GetProperty("authPolicyRevision").GetString(),
                "the planned auth_policy_revision binding is the manifest's authPolicyRevision");
    }

    [ArchitectureTest]
    public void FixtureManifest_AgreesWithTheSharedPythonProjection()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var manifest = ReadManifest(root);
        var projection = manifest.RootElement.GetProperty("pythonProjection");

        var relativePath = projection.GetProperty("path").GetString()!;
        var absolutePath = ArchitectureTestHelpers.CombinePath([root, .. relativePath.Split('/')]);
        File.Exists(absolutePath).Should().BeTrue($"{relativePath} is the Python projection of this manifest");

        var (literals, aliases) = ParsePythonConstants(File.ReadAllText(absolutePath));

        foreach (var symbol in projection.GetProperty("symbols").EnumerateObject())
        {
            literals.Should().ContainKey(symbol.Name,
                $"{relativePath} must define {symbol.Name} as a literal");
            DescribeJson(symbol.Value).Should().Be(literals[symbol.Name],
                $"{symbol.Name} must agree between the manifest and {relativePath}");
        }

        foreach (var alias in projection.GetProperty("aliases").EnumerateObject())
        {
            aliases.Should().ContainKey(alias.Name, $"{relativePath} must define {alias.Name}");
            aliases[alias.Name].Should().Be(alias.Value.GetString(),
                $"{alias.Name} must alias {alias.Value.GetString()} rather than duplicate its literal");
        }

        // The identity the projection publishes is the identity the manifest freezes.
        var symbols = projection.GetProperty("symbols");
        var canonicalService = manifest.RootElement.GetProperty("identities").GetProperty("services")
            .EnumerateArray().First(service => service.GetProperty("role").GetString() == "canonical-vector");
        symbols.GetProperty("SERVICE_ID").GetString().Should().Be(
            canonicalService.GetProperty("serviceId").GetString(),
            "the Python projection and the identity block name the same canonical service");
        var canonicalLayer = canonicalService.GetProperty("layers").EnumerateArray().Single();
        symbols.GetProperty("COLLECTION_ID").GetString().Should().Be(
            canonicalLayer.GetProperty("layerId").GetString(),
            "the OGC collection id is the canonical layer id");
        symbols.GetProperty("TOTAL_FEATURES").GetInt32().Should().Be(
            canonicalLayer.GetProperty("featureCount").GetInt32(),
            "the seeded feature count is published once");
        symbols.GetProperty("FEATURE_ID_FIELD").GetString().Should().Be(
            canonicalLayer.GetProperty("featureIdField").GetString(),
            "the feature id field is published once");
        symbols.GetProperty("ATTRIBUTE_FIELDS").EnumerateArray().Select(field => field.GetString()!)
            .Should().Equal(
                canonicalLayer.GetProperty("attributeFields").EnumerateArray().Select(field => field.GetString()!),
                "the attribute schema is published once, in one order");
    }

    [ArchitectureTest]
    public void FixtureManifest_CoversEveryActiveLaneAndEveryDeclaredCase()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var manifest = ReadManifest(root);
        using var matrix = JsonDocument.Parse(File.ReadAllText(
            ArchitectureTestHelpers.CombinePath(root, "docs", "gis", "data", "client-certification-matrix.v1.json")));

        var lanes = manifest.RootElement.GetProperty("laneBindings").EnumerateArray()
            .ToDictionary(lane => lane.GetProperty("laneId").GetString()!, lane => lane, StringComparer.Ordinal);

        foreach (var lane in matrix.RootElement.GetProperty("lanes").EnumerateArray())
        {
            var laneId = lane.GetProperty("id").GetString()!;
            lanes.Should().ContainKey(laneId,
                $"active lane {laneId} must bind a fixture projection or a governed not-applicable mapping");

            var bindings = lanes[laneId].GetProperty("protocols").EnumerateArray().ToArray();
            bindings.Should().NotBeEmpty($"active lane {laneId} must declare at least one protocol binding");

            foreach (var binding in bindings)
            {
                var protocol = binding.GetProperty("protocol").GetString();
                binding.TryGetProperty("fixtureProjection", out var fixtureProjection).Should().BeTrue(
                    $"{laneId}/{protocol} must name the fixture it runs against");
                fixtureProjection.GetProperty("target").GetString().Should().NotBeNullOrWhiteSpace();
                binding.GetProperty("applicableCases").GetArrayLength().Should().BeGreaterThan(0,
                    $"{laneId}/{protocol} would otherwise be an all-not-applicable placeholder");
            }
        }

        var reasons = manifest.RootElement.GetProperty("notApplicableReasons").EnumerateArray()
            .Select(reason => reason.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var facets = manifest.RootElement.GetProperty("scenarioFacets").EnumerateArray()
            .Select(facet => facet.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var cases = manifest.RootElement.GetProperty("cases").EnumerateArray()
            .ToDictionary(item => item.GetProperty("id").GetString()!,
                          item => item.GetProperty("scenarioFacetId").GetString()!,
                          StringComparer.Ordinal);

        cases.Values.Should().OnlyContain(facet => facets.Contains(facet),
            "every case maps to a declared scenario facet id");

        var matrixCases = matrix.RootElement.GetProperty("testCases").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        cases.Keys.Should().BeEquivalentTo(matrixCases,
            "the manifest case catalogue and the certification matrix declare the same case ids in both directions");

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lane in manifest.RootElement.GetProperty("laneBindings").EnumerateArray())
        {
            var laneId = lane.GetProperty("laneId").GetString();
            foreach (var binding in lane.GetProperty("protocols").EnumerateArray())
            {
                var protocol = binding.GetProperty("protocol").GetString();
                var applicable = binding.GetProperty("applicableCases").EnumerateArray()
                    .Select(item => item.GetString()!).ToHashSet(StringComparer.Ordinal);
                var notApplicable = new HashSet<string>(StringComparer.Ordinal);

                foreach (var excused in binding.GetProperty("notApplicableCases").EnumerateObject())
                {
                    reasons.Should().Contain(excused.Name,
                        $"{laneId}/{protocol} may only excuse a case with a governed reason");
                    foreach (var caseId in excused.Value.EnumerateArray())
                    {
                        notApplicable.Add(caseId.GetString()!);
                    }
                }

                applicable.Should().NotIntersectWith(notApplicable,
                    $"{laneId}/{protocol} cannot both require and excuse the same case");

                referenced.UnionWith(applicable);
                referenced.UnionWith(notApplicable);
                if (binding.TryGetProperty("extensionCases", out var extensions))
                {
                    referenced.UnionWith(extensions.EnumerateArray().Select(item => item.GetString()!));
                }
            }
        }

        referenced.Should().BeSubsetOf(cases.Keys, "a lane cannot reference an undeclared case id");

        var unbound = manifest.RootElement.GetProperty("unboundCases").EnumerateArray().ToArray();
        foreach (var entry in unbound)
        {
            entry.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace();
            entry.GetProperty("trackingIssue").GetString().Should().StartWith(TrackingIssuePrefix);
        }

        var unboundIds = unbound.Select(entry => entry.GetProperty("caseId").GetString()!).ToHashSet(StringComparer.Ordinal);
        unboundIds.Should().NotIntersectWith(referenced, "an unbound case cannot also be bound");
        cases.Keys.Except(referenced).Should().BeEquivalentTo(unboundIds,
            "every declared case is either bound by a lane or governed as unbound");

        var policy = manifest.RootElement.GetProperty("policy").GetProperty("failClosed");
        policy.GetProperty("applicableButUnexecuted").GetProperty("gateOutcome").GetString()
            .Should().Be("fail", "applicable-but-unexecuted must fail closed");
        policy.GetProperty("unsupported").GetProperty("requires").EnumerateArray()
            .Select(item => item.GetString()).Should().Contain("notApplicableReason",
                "unsupported cases require a governed not-applicable reason");
        policy.GetProperty("placeholderBaseline").GetProperty("gateOutcome").GetString()
            .Should().Be("reject", "all-skip placeholder baselines are rejected");

        manifest.RootElement.GetProperty("exceptions").EnumerateArray()
            .Select(item => item.GetProperty("laneFamily").GetString()).Should().Contain("ogc-cite",
                "the OGC CITE specification-owned-fixture exception is carried forward from the matrix");
    }

    [ArchitectureTest]
    public void FixtureManifest_AuthProfiles_AreRealizedOrGovernedAsGaps()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var manifest = ReadManifest(root);
        var document = manifest.RootElement;

        var inputs = document.GetProperty("inputs").EnumerateArray()
            .Select(input => input.GetProperty("path").GetString()!).ToHashSet(StringComparer.Ordinal);
        var gaps = document.GetProperty("gaps").EnumerateArray()
            .Select(gap => gap.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);

        var profiles = document.GetProperty("authPolicy").GetProperty("profiles").EnumerateArray().ToArray();
        var profileIds = profiles.Select(profile => profile.GetProperty("id").GetString()!).ToArray();

        profileIds.Should().Contain(RequiredAuthProfiles,
            "the 2026.1 auth contract enumerates all seven required profiles plus the invalid/expired split");
        profileIds.Should().OnlyHaveUniqueItems();

        foreach (var profile in profiles)
        {
            var id = profile.GetProperty("id").GetString();
            var status = profile.GetProperty("status").GetString();
            status.Should().BeOneOf("realized", "realized-not-asserted", "gap");

            if (status is "realized" or "realized-not-asserted")
            {
                var fixtures = profile.GetProperty("realizedByFixture").EnumerateArray()
                    .Select(item => item.GetString()!).ToArray();
                fixtures.Should().NotBeEmpty($"auth profile {id} claims realization, so it must name a fixture");
                fixtures.Should().BeSubsetOf(inputs, $"auth profile {id} must be realized by a manifest input");
                inputs.Should().Contain(profile.GetProperty("realizedByServerConfig").GetString()!,
                    $"auth profile {id} must name a server configuration this manifest addresses");
            }

            if (status is "gap" or "realized-not-asserted")
            {
                var gapId = profile.GetProperty("gapId").GetString()!;
                gaps.Should().Contain(gapId, $"auth profile {id} must point at a recorded gap");
            }
        }

        // No secret beyond the well-known non-production fixture key may appear.
        document.GetProperty("authPolicy").GetProperty("secretMaterial").GetString().Should().Be("none");
        var mechanisms = document.GetProperty("authPolicy").GetProperty("mechanisms").EnumerateArray().ToArray();
        mechanisms.Should().NotBeEmpty();
        mechanisms.Select(mechanism => mechanism.GetProperty("id").GetString())
            .Should().Contain("admin-api-key", "the control-plane mechanism is an API key, not a password login");
        var apiKey = mechanisms.Single(mechanism => mechanism.GetProperty("id").GetString() == "admin-api-key");
        apiKey.GetProperty("header").GetString().Should().Be("X-API-Key");
        apiKey.GetProperty("valueSource").GetString().Should().Be("HONUA_ADMIN_PASSWORD");
    }

    [ArchitectureTest]
    public void FixtureManifest_AndProseContract_DocumentTheSameCasesAndAuthProfiles()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var manifest = ReadManifest(root);
        var contractPath = ArchitectureTestHelpers.CombinePath(
            root, "docs", "gis", "CLIENT_CERTIFICATION_FIXTURE_CONTRACT.md");
        File.Exists(contractPath).Should().BeTrue("the manifest is paired 1:1 with its prose contract");
        var contract = File.ReadAllText(contractPath);

        manifest.RootElement.GetProperty("proseContract").GetString()
            .Should().Be("docs/gis/CLIENT_CERTIFICATION_FIXTURE_CONTRACT.md");

        var caseIds = manifest.RootElement.GetProperty("cases").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!).ToArray();
        var documentedCases = Regex.Matches(contract, @"`(?<id>(?:CERT|JS|EL|DSK|CLI|BI|NB)-[A-Z0-9-]*\d)`")
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);
        documentedCases.Should().BeEquivalentTo(caseIds,
            "the prose contract documents exactly the manifest's case-id set, in both directions");

        var profileIds = manifest.RootElement.GetProperty("authPolicy").GetProperty("profiles")
            .EnumerateArray().Select(item => item.GetProperty("id").GetString()!).ToArray();
        var profileSection = SliceBetween(contract, "<!-- auth-profiles:begin -->", "<!-- auth-profiles:end -->");
        var documentedProfiles = Regex.Matches(profileSection, @"^\| `(?<id>[a-z][a-z0-9-]*)` \|", RegexOptions.Multiline)
            .Select(match => match.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);
        documentedProfiles.Should().BeEquivalentTo(profileIds,
            "the prose contract documents exactly the manifest's auth-profile set, in both directions");

        foreach (var revision in PublishedRevisions)
        {
            contract.Should().Contain(manifest.RootElement.GetProperty(revision).GetString()!,
                $"the prose contract publishes the current {revision}");
        }
    }

    [ArchitectureTest]
    public void FixtureManifest_Gaps_AreHonestAndTracked()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var manifest = ReadManifest(root);
        var document = manifest.RootElement;

        var gaps = document.GetProperty("gaps").EnumerateArray().ToArray();
        gaps.Should().NotBeEmpty("a manifest that claims no gap at all is claiming coverage it does not have");

        foreach (var gap in gaps)
        {
            var id = gap.GetProperty("id").GetString();
            id.Should().NotBeNullOrWhiteSpace();
            gap.GetProperty("area").GetString().Should().NotBeNullOrWhiteSpace($"gap {id} must name its area");
            gap.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace($"gap {id} must state why");
            gap.GetProperty("trackingIssue").GetString().Should().StartWith(TrackingIssuePrefix,
                $"gap {id} must be tracked by a honua-io issue");
        }

        var gapIds = gaps.Select(gap => gap.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        gapIds.Count.Should().Be(gaps.Length, "gap ids are stable unique identities");

        var referenced = new HashSet<string>(StringComparer.Ordinal);
        foreach (var coverage in document.GetProperty("vectorSeedCoverage").EnumerateArray())
        {
            if (coverage.TryGetProperty("gapId", out var gapId))
            {
                referenced.Add(gapId.GetString()!);
            }
            else
            {
                coverage.GetProperty("evidence").GetString().Should().NotBeNullOrWhiteSpace(
                    $"coverage claim {coverage.GetProperty("dimension").GetString()} must cite the seed evidence behind it");
            }
        }

        foreach (var supporting in document.GetProperty("supportingFixtures").EnumerateObject())
        {
            var status = supporting.Value.GetProperty("status").GetString();
            status.Should().BeOneOf("realized", "partial", "not-required");
            if (supporting.Value.TryGetProperty("gapId", out var gapId))
            {
                referenced.Add(gapId.GetString()!);
            }
            else
            {
                status.Should().NotBe("partial",
                    $"supporting fixture {supporting.Name} is partial, so it must record a gap");
            }
        }

        foreach (var profile in document.GetProperty("authPolicy").GetProperty("profiles").EnumerateArray())
        {
            if (profile.TryGetProperty("gapId", out var gapId))
            {
                referenced.Add(gapId.GetString()!);
            }
        }

        referenced.Should().BeSubsetOf(gapIds, "every gapId reference resolves to a recorded gap");

        document.GetProperty("outOfScope").GetProperty("issue").GetString()
            .Should().Be("https://github.com/honua-io/honua-server/issues/3435",
                "2026.2 fixture depth stays fenced out of this core");
    }

    // -- helpers ------------------------------------------------------------

    private static string SliceBetween(string source, string startMarker, string endMarker)
    {
        var start = source.IndexOf(startMarker, StringComparison.Ordinal);
        start.Should().BeGreaterThanOrEqualTo(0, $"the contract must carry the {startMarker} marker");
        var end = source.IndexOf(endMarker, start, StringComparison.Ordinal);
        end.Should().BeGreaterThan(start, $"the contract must carry the {endMarker} marker after {startMarker}");
        return source[(start + startMarker.Length)..end];
    }

    private static JsonDocument ReadManifest(string root)
        => JsonDocument.Parse(File.ReadAllText(
            ArchitectureTestHelpers.CombinePath(root, "docs", "gis", "data", ManifestRelativePath)));

    private static string ComputeFileDigest(string path)
        => ComputeDigest(File.ReadAllBytes(path));

    private static string ComputeDigest(byte[] payload)
        => "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

    /// <summary>
    /// honua.input-set-digest/v1: sha256sum-shaped lines for one role's inputs,
    /// ordered by the UTF-8 bytes of the repo-relative path, hashed once more.
    /// </summary>
    private static string ComputeInputSetDigest(JsonElement manifest, string role)
    {
        var lines = manifest.GetProperty("inputs").EnumerateArray()
            .Where(input => string.Equals(input.GetProperty("role").GetString(), role, StringComparison.Ordinal))
            .Select(input => (
                Path: input.GetProperty("path").GetString()!,
                Digest: input.GetProperty("sha256").GetString()!))
            .OrderBy(entry => entry.Path, StringComparer.Ordinal)
            .Select(entry => $"{entry.Digest.Split(':', 2)[1]}  {entry.Path}\n");

        return ComputeDigest(Encoding.UTF8.GetBytes(string.Concat(lines)));
    }

    /// <summary>
    /// honua.canonical-json-digest/v1 serialization: members sorted by ordinal name,
    /// no insignificant whitespace, non-ASCII emitted literally, numbers rejected.
    /// </summary>
    private static string CanonicalJson(JsonElement element)
    {
        var builder = new StringBuilder();
        WriteCanonical(element, builder);
        return builder.ToString();
    }

    private static void WriteCanonical(JsonElement element, StringBuilder builder)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                builder.Append('{');
                var first = true;
                foreach (var property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    WriteCanonicalString(property.Name, builder);
                    builder.Append(':');
                    WriteCanonical(property.Value, builder);
                }

                builder.Append('}');
                break;
            case JsonValueKind.Array:
                builder.Append('[');
                var firstItem = true;
                foreach (var item in element.EnumerateArray())
                {
                    if (!firstItem)
                    {
                        builder.Append(',');
                    }

                    firstItem = false;
                    WriteCanonical(item, builder);
                }

                builder.Append(']');
                break;
            case JsonValueKind.String:
                WriteCanonicalString(element.GetString()!, builder);
                break;
            case JsonValueKind.True:
                builder.Append("true");
                break;
            case JsonValueKind.False:
                builder.Append("false");
                break;
            case JsonValueKind.Null:
                builder.Append("null");
                break;
            default:
                throw new InvalidOperationException(
                    $"{element.ValueKind} is not permitted inside a canonically digested object; " +
                    "numbers would make the digest depend on a numeric formatter.");
        }
    }

    private static void WriteCanonicalString(string value, StringBuilder builder)
    {
        builder.Append('"');
        foreach (var character in value)
        {
            switch (character)
            {
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\\':
                    builder.Append("\\\\");
                    break;
                case '\n':
                    builder.Append("\\n");
                    break;
                case '\r':
                    builder.Append("\\r");
                    break;
                case '\t':
                    builder.Append("\\t");
                    break;
                case '\b':
                    builder.Append("\\b");
                    break;
                case '\f':
                    builder.Append("\\f");
                    break;
                default:
                    if (character < 0x20)
                    {
                        builder.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
                    }
                    else
                    {
                        builder.Append(character);
                    }

                    break;
            }
        }

        builder.Append('"');
    }

    /// <summary>
    /// Renders a manifest JSON value into the same normalized text the Python
    /// literal parser produces, so the two can be compared without executing Python.
    /// </summary>
    private static string DescribeJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => "s:" + element.GetString(),
        JsonValueKind.Number => "n:" + element.GetDouble().ToString("R", CultureInfo.InvariantCulture),
        JsonValueKind.True => "b:true",
        JsonValueKind.False => "b:false",
        JsonValueKind.Array => "[" + string.Join(",", element.EnumerateArray().Select(DescribeJson)) + "]",
        _ => throw new InvalidOperationException($"unsupported projection value kind {element.ValueKind}"),
    };

    private static (Dictionary<string, string> Literals, Dictionary<string, string> Aliases)
        ParsePythonConstants(string source)
    {
        var literals = new Dictionary<string, string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in ConstantAssignmentPattern.Matches(source))
        {
            var name = match.Groups["name"].Value;
            var raw = ReadPythonValue(source, match.Index + match.Length);
            if (raw is null)
            {
                continue;
            }

            var trimmed = raw.Trim();
            if (IdentifierPattern.IsMatch(trimmed))
            {
                aliases[name] = trimmed;
                continue;
            }

            if (TryParsePythonLiteral(trimmed, out var described))
            {
                literals[name] = described;
            }
        }

        return (literals, aliases);
    }

    private static string? ReadPythonValue(string source, int start)
    {
        if (start >= source.Length)
        {
            return null;
        }

        if (source[start] == '(')
        {
            var depth = 0;
            var index = start;
            while (index < source.Length)
            {
                var character = source[index];
                if (character is '"' or '\'')
                {
                    index = SkipPythonString(source, index);
                    continue;
                }

                if (character == '(')
                {
                    depth++;
                }
                else if (character == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return source[start..(index + 1)];
                    }
                }

                index++;
            }

            return null;
        }

        var newline = source.IndexOf('\n', start);
        return newline < 0 ? source[start..] : source[start..newline];
    }

    private static int SkipPythonString(string source, int index)
    {
        var quote = source[index];
        index++;
        while (index < source.Length)
        {
            if (source[index] == '\\')
            {
                index += 2;
                continue;
            }

            if (source[index] == quote)
            {
                return index + 1;
            }

            index++;
        }

        return index;
    }

    private static bool TryParsePythonLiteral(string raw, out string described)
    {
        described = string.Empty;
        var text = raw.Trim();

        if (text.StartsWith('(') && text.EndsWith(')'))
        {
            var parts = SplitPythonTuple(text[1..^1]);
            var rendered = new List<string>(parts.Count);
            foreach (var part in parts)
            {
                if (!TryParsePythonLiteral(part, out var item))
                {
                    return false;
                }

                rendered.Add(item);
            }

            described = "[" + string.Join(",", rendered) + "]";
            return true;
        }

        text = StripPythonComment(text);

        if (text.Length >= 2 && (text[0] == '"' || text[0] == '\'') && text[^1] == text[0])
        {
            described = "s:" + UnescapePythonString(text[1..^1]);
            return true;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            described = "n:" + number.ToString("R", CultureInfo.InvariantCulture);
            return true;
        }

        if (string.Equals(text, "True", StringComparison.Ordinal) ||
            string.Equals(text, "False", StringComparison.Ordinal))
        {
            described = "b:" + text.ToLowerInvariant();
            return true;
        }

        return false;
    }

    private static List<string> SplitPythonTuple(string body)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var depth = 0;
        var index = 0;

        while (index < body.Length)
        {
            var character = body[index];
            if (character is '"' or '\'')
            {
                var end = SkipPythonString(body, index);
                current.Append(body[index..end]);
                index = end;
                continue;
            }

            if (character == '#')
            {
                var newline = body.IndexOf('\n', index);
                index = newline < 0 ? body.Length : newline;
                continue;
            }

            if (character is '(' or '[')
            {
                depth++;
            }
            else if (character is ')' or ']')
            {
                depth--;
            }

            if (character == ',' && depth == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
                index++;
                continue;
            }

            current.Append(character);
            index++;
        }

        parts.Add(current.ToString());
        return parts.Select(part => part.Trim()).Where(part => part.Length > 0).ToList();
    }

    private static string StripPythonComment(string text)
    {
        var index = 0;
        while (index < text.Length)
        {
            var character = text[index];
            if (character is '"' or '\'')
            {
                index = SkipPythonString(text, index);
                continue;
            }

            if (character == '#')
            {
                return text[..index].Trim();
            }

            index++;
        }

        return text.Trim();
    }

    private static string UnescapePythonString(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                builder.Append(value[index]);
                continue;
            }

            index++;
            builder.Append(value[index] switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '"' => '"',
                '\'' => '\'',
                _ => value[index],
            });
        }

        return builder.ToString();
    }
}
