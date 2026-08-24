// Copyright 2025 Honua Authors
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace Honua.Architecture.Tests;

/// <summary>
/// Guards <c>docs/gis/data/client-certification-roster.v1.json</c>, the repository-owned
/// projection of the full client-certification roster (honua-server#3395, parent #3389).
///
/// The roster replaces an external Claude artifact as the authority for which clients exist
/// and what status each one holds. These tests keep it closed and honest:
/// every entry is classified exactly once, the reconciled count is arithmetically justified,
/// identities cannot collide across statuses, the projection stays joined to
/// <c>client-certification-matrix.v1.json</c> in both directions, the prose pair documents the
/// same id set, and no planned or excluded row can ever pass or block a gate.
/// </summary>
public sealed class ClientCertificationRosterTests
{
    private static readonly Regex ProseEntryPattern = new(
        @"^### `(?<id>[a-z0-9-]+)` - ",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex KebabCasePattern = new(
        @"^[a-z0-9]+(-[a-z0-9]+)*$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private const string HonuaIssuePrefix = "https://github.com/honua-io/";

    private static readonly string[] RequiredEntryFields =
    [
        "displayName", "family", "clientVersionPolicy", "activationState",
        "requiredTier", "intendedTierOnActivation", "runtime", "owningIssue",
    ];

    private static readonly JsonValueKind[] BooleanValueKinds = [JsonValueKind.True, JsonValueKind.False];

    private static readonly string[] ProjectionConsumerRepositories =
    [
        "honua-io/honua-release", "honua-io/honua-evidence",
    ];

    [ArchitectureTest]
    public void Roster_ReconciledCountIsJustifiedByEveryReconciliationNote()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var roster = ReadRoster(root);
        var document = roster.RootElement;
        var entries = Entries(document);
        var source = document.GetProperty("rosterSource");

        source.GetProperty("authority").GetString().Should().Be("repository",
            "the repository file, not the external artifact, is the roster authority");
        source.GetProperty("historicalArtifactUrl").GetString().Should()
            .Be("https://claude.ai/code/artifact/0122304a-cbf1-46a0-bc33-61826665bc94");
        source.GetProperty("authorityStatement").GetString().Should()
            .Contain("non-authoritative", "the historical artifact must be explicitly demoted");

        var declared = source.GetProperty("declaredEntryCount").GetInt32();
        var reconciled = source.GetProperty("reconciledEntryCount").GetInt32();
        entries.Length.Should().Be(reconciled, "reconciledEntryCount must equal the number of entries");

        var notes = document.GetProperty("reconciliationNotes").EnumerateArray().ToArray();
        notes.Should().NotBeEmpty("the count difference must be explained, never silently padded or dropped");

        var deltaSum = notes.Sum(note => note.GetProperty("delta").GetInt32());
        (declared + deltaSum).Should().Be(reconciled,
            "the reconciliation note deltas must account for every difference between the artifact's " +
            "headline tally and the reconciled roster");

        var ids = entries.Select(Id).ToHashSet(StringComparer.Ordinal);
        var noteIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var note in notes)
        {
            var noteId = note.GetProperty("id").GetString()!;
            noteIds.Add(noteId).Should().BeTrue($"reconciliation note id {noteId} must be unique");
            note.GetProperty("note").GetString().Should().NotBeNullOrWhiteSpace(
                $"reconciliation note {noteId} must explain itself");

            var affected = note.GetProperty("affectedEntryIds").EnumerateArray()
                .Select(value => value.GetString()!).ToArray();
            affected.Should().NotBeEmpty(
                $"reconciliation note {noteId} must name the entries it accounts for, otherwise it is unused");
            affected.Should().BeSubsetOf(ids,
                $"reconciliation note {noteId} must only reference entries that exist in the roster");
        }

        var artifactRows = entries.Count(entry => string.Equals(
            entry.GetProperty("rosterOrigin").GetString(), "artifact", StringComparison.Ordinal));
        var repositoryRows = entries.Count(entry => string.Equals(
            entry.GetProperty("rosterOrigin").GetString(), "repository-registry", StringComparison.Ordinal));
        artifactRows.Should().Be(source.GetProperty("artifactEnumeratedEntryCount").GetInt32());
        repositoryRows.Should().Be(source.GetProperty("repositoryOnlyEntryCount").GetInt32());
        (artifactRows + repositoryRows).Should().Be(reconciled,
            "every entry must be attributed either to the artifact or to this repository's registry");
    }

    [ArchitectureTest]
    public void Roster_IdentitiesAreUniqueAndCannotCollideAcrossStatuses()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var roster = ReadRoster(root);
        var entries = Entries(roster.RootElement);

        var ids = entries.Select(Id).ToArray();
        ids.Should().OnlyHaveUniqueItems("roster ids are stable unique client identities");
        foreach (var id in ids)
        {
            KebabCasePattern.IsMatch(id).Should().BeTrue($"roster id {id} must be stable kebab-case");
        }

        var active = IdsWithStatus(entries, "active");
        var planned = IdsWithStatus(entries, "planned");
        var excluded = IdsWithStatus(entries, "excluded");

        active.Should().NotIntersectWith(planned, "a client cannot be active and planned simultaneously");
        active.Should().NotIntersectWith(excluded, "an active client cannot also be excluded");
        planned.Should().NotIntersectWith(excluded, "a planned client cannot also be excluded");
        (active.Count + planned.Count + excluded.Count).Should().Be(entries.Length,
            "every entry must carry exactly one of the three governed statuses");
    }

    [ArchitectureTest]
    public void Roster_EveryEntryDeclaresTheFieldsItsStatusRequires()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var roster = ReadRoster(root);
        var document = roster.RootElement;
        var entries = Entries(document);
        var vocabulary = document.GetProperty("vocabulary");

        var statuses = StringSet(vocabulary, "status");
        var activationStates = StringSet(vocabulary, "activationState");
        var requiredTiers = StringSet(vocabulary, "requiredTier");
        var runtimes = StringSet(vocabulary, "runtime");
        var roles = StringSet(vocabulary, "laneBindingRole");
        var fixtureKinds = StringSet(vocabulary, "fixtureProjectionKind");
        var targetReleases = StringSet(vocabulary, "targetRelease");
        var operationFamilies = StringSet(vocabulary, "operationFamilies");
        var scenarioFacets = StringSet(vocabulary, "scenarioFacets");

        foreach (var entry in entries)
        {
            var id = Id(entry);
            var status = entry.GetProperty("status").GetString()!;
            statuses.Should().Contain(status, $"{id} must declare a governed status");

            foreach (var field in RequiredEntryFields)
            {
                entry.GetProperty(field).GetString().Should().NotBeNullOrWhiteSpace(
                    $"{id} must declare a non-empty {field}");
            }

            activationStates.Should().Contain(entry.GetProperty("activationState").GetString()!,
                $"{id} must declare a governed activation state");
            requiredTiers.Should().Contain(entry.GetProperty("requiredTier").GetString()!,
                $"{id} must declare a governed required tier");
            requiredTiers.Should().Contain(entry.GetProperty("intendedTierOnActivation").GetString()!,
                $"{id} must declare a governed intended tier");
            runtimes.Should().Contain(entry.GetProperty("runtime").GetString()!,
                $"{id} must declare a governed runtime substrate");
            roles.Should().Contain(entry.GetProperty("laneBinding").GetProperty("role").GetString()!,
                $"{id} must declare a governed lane-binding role");
            fixtureKinds.Should().Contain(entry.GetProperty("fixtureProjection").GetProperty("kind").GetString()!,
                $"{id} must declare a governed fixture projection kind");

            var applicable = Families(entry, "applicable");
            var notApplicable = NotApplicableFamilies(entry);
            applicable.Should().OnlyHaveUniqueItems($"{id} must not list a family twice as applicable");
            notApplicable.Should().OnlyHaveUniqueItems($"{id} must not list a family twice as not-applicable");
            applicable.Should().NotIntersectWith(notApplicable,
                $"{id} cannot call the same operation family both applicable and not-applicable");
            applicable.Concat(notApplicable).ToHashSet(StringComparer.Ordinal)
                .Should().BeEquivalentTo(operationFamilies,
                    $"{id} must classify every CERT-* operation family so the client denominator is closed");
            foreach (var item in entry.GetProperty("operationApplicability")
                         .GetProperty("notApplicable").EnumerateArray())
            {
                item.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace(
                    $"{id} must give a governed reason for every structurally not-applicable family");
            }

            entry.GetProperty("scenarioFacets").EnumerateArray().Select(value => value.GetString()!)
                .Should().BeSubsetOf(scenarioFacets, $"{id} must use the governed scenario-facet vocabulary");

            switch (status)
            {
                case "planned":
                    targetReleases.Should().Contain(entry.GetProperty("targetRelease").GetString()!,
                        $"planned entry {id} must declare a target release");
                    entry.GetProperty("owningIssue").GetString().Should().StartWith(HonuaIssuePrefix,
                        $"planned entry {id} must link an implementation issue");
                    entry.GetProperty("exclusionRationale").ValueKind.Should().Be(JsonValueKind.Null,
                        $"planned entry {id} is not excluded");
                    break;

                case "excluded":
                    entry.GetProperty("targetRelease").ValueKind.Should().Be(JsonValueKind.Null,
                        $"excluded entry {id} must not declare a target release");
                    entry.GetProperty("exclusionRationale").GetString().Should().NotBeNullOrWhiteSpace(
                        $"excluded entry {id} must record a governed rationale");
                    entry.GetProperty("coveredAlternative").GetString().Should().NotBeNullOrWhiteSpace(
                        $"excluded entry {id} must name what covers it instead");
                    break;

                default:
                    targetReleases.Should().Contain(entry.GetProperty("targetRelease").GetString()!,
                        $"active entry {id} must declare the release it is eligible for");
                    entry.GetProperty("evidenceProducer").GetString().Should().NotBeNullOrWhiteSpace(
                        $"active entry {id} must name the producer that emits its evidence");
                    entry.GetProperty("exclusionRationale").ValueKind.Should().Be(JsonValueKind.Null,
                        $"active entry {id} is not excluded");
                    entry.GetProperty("scenarioFacets").GetArrayLength().Should().BeGreaterThan(0,
                        $"active entry {id} must declare the scenario facets it certifies");
                    break;
            }
        }
    }

    [ArchitectureTest]
    public void Roster_IsJoinedToTheCertificationMatrixInBothDirections()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var roster = ReadRoster(root);
        using var matrix = ReadMatrix(root);
        var entries = Entries(roster.RootElement);
        var byId = entries.ToDictionary(Id, entry => entry, StringComparer.Ordinal);

        var lanes = matrix.RootElement.GetProperty("lanes").EnumerateArray()
            .ToDictionary(lane => lane.GetProperty("id").GetString()!, lane => lane, StringComparer.Ordinal);
        var plannedLanes = matrix.RootElement.GetProperty("plannedLanes").EnumerateArray()
            .ToDictionary(lane => lane.GetProperty("id").GetString()!, lane => lane, StringComparer.Ordinal);
        var exclusions = matrix.RootElement.GetProperty("exclusions").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!).ToHashSet(StringComparer.Ordinal);
        var declaredCaseIds = matrix.RootElement.GetProperty("testCases").EnumerateArray()
            .Select(item => item.GetProperty("id").GetString()!).ToArray();

        // Forward: every registered matrix identity must appear in the roster with the matching status.
        foreach (var (laneId, lane) in lanes)
        {
            byId.Should().ContainKey(laneId,
                "an active certification lane may never exist without a roster row; add it to " +
                "client-certification-roster.v1.json in the same change");
            var entry = byId[laneId];
            entry.GetProperty("status").GetString().Should().Be("active",
                $"matrix lane {laneId} is registered as active");
            entry.GetProperty("laneBinding").GetProperty("role").GetString().Should().Be("lane",
                $"roster row {laneId} owns the matrix lane id");
            entry.GetProperty("requiredTier").GetString().Should().Be(lane.GetProperty("requiredTier").GetString(),
                $"the roster and the matrix must agree on the execution tier for {laneId}");

            // The matrix declares applicability per case id; the roster classifies per
            // operation family. A family therefore only counts as not-applicable for the
            // roster when the matrix declares *every* case in it not-applicable — either
            // through a `CERT-RNDR-*` wildcard or by naming each id. A partially
            // applicable family stays applicable in the roster: `py-owslib` substantiates
            // CERT-RNDR-01 from WMS/WMTS imagery while the remaining seven CERT-RNDR ids
            // have no drawing surface to observe, and collapsing that to "rendering is
            // not applicable to OWSLib" would erase real, committed evidence.
            var matrixNotApplicable = WhollyNotApplicableFamilies(lane, declaredCaseIds);
            matrixNotApplicable.Should().BeSubsetOf(NotApplicableFamilies(entry),
                $"roster row {laneId} must preserve the operation families the matrix declares " +
                "wholly not-applicable");
        }

        foreach (var (laneId, lane) in plannedLanes)
        {
            byId.Should().ContainKey(laneId,
                "a planned certification lane may never exist without a roster row; add it to " +
                "client-certification-roster.v1.json in the same change");
            var entry = byId[laneId];
            entry.GetProperty("status").GetString().Should().Be("planned",
                $"matrix planned lane {laneId} has not landed real evidence");
            entry.GetProperty("laneBinding").GetProperty("role").GetString().Should().Be("planned-lane");
            entry.GetProperty("intendedTierOnActivation").GetString().Should()
                .Be(lane.GetProperty("requiredTier").GetString(),
                    $"the roster must carry the tier the matrix intends for {laneId} once it activates");
            lane.GetProperty("protocols").EnumerateArray().Select(value => value.GetString()!)
                .Should().BeSubsetOf(Protocols(entry),
                    $"roster row {laneId} must cover every protocol the matrix declares for the planned lane");
        }

        foreach (var exclusionId in exclusions)
        {
            byId.Should().ContainKey(exclusionId, "a governed exclusion must also be a roster row");
            byId[exclusionId].GetProperty("status").GetString().Should().Be("excluded");
            byId[exclusionId].GetProperty("laneBinding").GetProperty("role").GetString().Should().Be("exclusion");
        }

        // Reverse: no roster row may claim a matrix binding the matrix does not have.
        foreach (var entry in entries)
        {
            var id = Id(entry);
            var binding = entry.GetProperty("laneBinding");
            var role = binding.GetProperty("role").GetString()!;
            var matrixLaneId = binding.GetProperty("matrixLaneId").ValueKind == JsonValueKind.Null
                ? null
                : binding.GetProperty("matrixLaneId").GetString();

            switch (role)
            {
                case "lane":
                    matrixLaneId.Should().Be(id);
                    lanes.Should().ContainKey(id, $"{id} claims to own a matrix lane");
                    break;
                case "sub-lane":
                    lanes.Should().ContainKey(matrixLaneId!,
                        $"{id} is a sub-lane of a matrix lane that must exist");
                    entry.GetProperty("status").GetString().Should().Be("active",
                        $"{id} emits under an active lane id");
                    break;
                case "planned-lane":
                    matrixLaneId.Should().Be(id);
                    plannedLanes.Should().ContainKey(id, $"{id} claims a matrix planned-lane row");
                    break;
                case "exclusion":
                    matrixLaneId.Should().Be(id);
                    exclusions.Should().Contain(id, $"{id} claims a matrix exclusion row");
                    break;
                default:
                    matrixLaneId.Should().BeNull(
                        $"{id} has role {role} and must not claim a matrix lane binding");
                    break;
            }
        }

        // Governed fixture exceptions must be the ones the matrix actually registers.
        var exceptionFamilies = matrix.RootElement.GetProperty("fixturePolicy").GetProperty("exceptions")
            .EnumerateArray().Select(item => item.GetProperty("laneFamily").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var fixtureProjection = entry.GetProperty("fixtureProjection");
            if (!string.Equals(fixtureProjection.GetProperty("kind").GetString(), "governed-exception", StringComparison.Ordinal))
            {
                continue;
            }

            exceptionFamilies.Should().Contain(fixtureProjection.GetProperty("exceptionLaneFamily").GetString()!,
                $"{Id(entry)} may only claim a fixture exception the matrix fixturePolicy registers");
        }

        // Committed evidence protocols must be covered by the rows bound to that lane.
        foreach (var (laneId, protocols) in ReadEvidenceProtocols(root))
        {
            var covered = entries
                .Where(entry => string.Equals(
                    entry.GetProperty("laneBinding").GetProperty("matrixLaneId").ValueKind == JsonValueKind.Null
                        ? null
                        : entry.GetProperty("laneBinding").GetProperty("matrixLaneId").GetString(),
                    laneId, StringComparison.Ordinal))
                .SelectMany(Protocols)
                .ToHashSet(StringComparer.Ordinal);
            protocols.Should().BeSubsetOf(covered,
                $"every protocol with committed {laneId} evidence must be declared by a roster row bound to that lane");
        }
    }

    [ArchitectureTest]
    public void Roster_OnlyActivatedRowsCanPassOrBlockAGate()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var roster = ReadRoster(root);
        var document = roster.RootElement;

        document.GetProperty("denominatorRules").GetProperty("eligible").GetString()
            .Should().Be("status == 'active' AND activationState == 'activated'");

        foreach (var entry in Entries(document))
        {
            var id = Id(entry);
            var isActivated = string.Equals(entry.GetProperty("status").GetString(), "active", StringComparison.Ordinal)
                && string.Equals(entry.GetProperty("activationState").GetString(), "activated", StringComparison.Ordinal);
            var requiredTier = entry.GetProperty("requiredTier").GetString()!;

            if (isActivated)
            {
                requiredTier.Should().NotBe("none",
                    $"{id} is activated, so it must declare the tier it participates in");
                continue;
            }

            requiredTier.Should().Be("none",
                $"{id} is not both active and activated, so it must be structurally incapable of passing " +
                "or blocking any gate");
        }
    }

    [ArchitectureTest]
    public void Roster_ProseProjectionDocumentsExactlyTheSameIdentities()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var roster = ReadRoster(root);
        var jsonIds = Entries(roster.RootElement).Select(Id).ToHashSet(StringComparer.Ordinal);

        var prose = File.ReadAllText(
            ArchitectureTestHelpers.CombinePath(root, "docs", "gis", "CLIENT_CERTIFICATION_ROSTER.md"));
        var proseIds = ProseEntryPattern.Matches(prose)
            .Select(match => match.Groups["id"].Value).ToHashSet(StringComparer.Ordinal);

        proseIds.Should().BeEquivalentTo(jsonIds,
            "the published roster and its machine-readable source must document the same identities in both directions");
        prose.Should().Contain("non-authoritative",
            "the prose projection must also demote the external artifact");
    }

    [ArchitectureTest]
    public void Roster_EveryOwningIssueIsAHonuaIssue()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var roster = ReadRoster(root);

        foreach (var entry in Entries(roster.RootElement))
        {
            entry.GetProperty("owningIssue").GetString().Should().StartWith(HonuaIssuePrefix,
                $"{Id(entry)} must be owned by a honua-io issue");
            entry.GetProperty("needsOwningIssue").ValueKind.Should()
                .BeOneOf(BooleanValueKinds,
                    $"{Id(entry)} must state whether a dedicated implementation issue still has to be filed");
        }
    }

    [ArchitectureTest]
    public void Roster_ProtocolSurfacesUseTheGovernedVocabulary()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var roster = ReadRoster(root);
        using var matrix = ReadMatrix(root);

        var protocolVocabulary = roster.RootElement.GetProperty("vocabulary").GetProperty("protocolSurfaces");
        var abbreviationTable = protocolVocabulary.GetProperty("fromMatrixAbbreviationTable")
            .EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        var extended = protocolVocabulary.GetProperty("extended").EnumerateArray().ToArray();
        var known = abbreviationTable
            .Concat(extended.Select(item => item.GetProperty("id").GetString()!))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var item in extended)
        {
            item.GetProperty("reason").GetString().Should().NotBeNullOrWhiteSpace(
                "every protocol outside the matrix abbreviation table must justify its presence");
        }

        var matrixProse = File.ReadAllText(
            ArchitectureTestHelpers.CombinePath(root, "docs", "gis", "CROSS_CLIENT_CERTIFICATION_MATRIX.md"));
        foreach (var protocol in abbreviationTable)
        {
            matrixProse.Should().Contain($"`{protocol}`",
                $"{protocol} claims to come from the matrix protocol-abbreviation table");
        }

        foreach (var entry in Entries(roster.RootElement))
        {
            Protocols(entry).Should().BeSubsetOf(known,
                $"{Id(entry)} must only declare protocols the roster vocabulary governs");
        }

        foreach (var lane in matrix.RootElement.GetProperty("plannedLanes").EnumerateArray())
        {
            lane.GetProperty("protocols").EnumerateArray().Select(value => value.GetString()!)
                .Should().BeSubsetOf(known,
                    "every protocol the matrix uses must be present in the roster vocabulary");
        }
    }

    [ArchitectureTest]
    public void Roster_DeclaresDownstreamProjectionWithoutCreatingASecondDenominator()
    {
        var root = ArchitectureTestHelpers.ResolveRepositoryRoot();
        using var roster = ReadRoster(root);
        var document = roster.RootElement;

        var binding = document.GetProperty("capabilityApplicabilityBinding");
        binding.GetProperty("authority").GetString().Should()
            .Be("https://github.com/honua-io/honua-server/issues/3387",
                "#3387 remains the capability-applicability authority; this roster feeds it");
        binding.GetProperty("joinKey").GetString().Should().Be("id");
        binding.GetProperty("mustNotDo").GetArrayLength().Should().BeGreaterThan(0);

        var consumers = document.GetProperty("projection").GetProperty("consumers").EnumerateArray().ToArray();
        consumers.Select(consumer => consumer.GetProperty("repository").GetString())
            .Should().BeEquivalentTo(ProjectionConsumerRepositories,
                "the roster must state exactly what the release and evidence repositories consume");

        var rosterPath = "honua-server:docs/gis/data/client-certification-roster.v1.json";
        foreach (var consumer in consumers)
        {
            consumer.GetProperty("issue").GetString().Should().StartWith(HonuaIssuePrefix);
            consumer.GetProperty("consumes").GetString().Should().Be(rosterPath);
            consumer.GetProperty("schemaVersion").GetString().Should()
                .Be(document.GetProperty("schemaVersion").GetString());
            consumer.GetProperty("denominatorFields").GetArrayLength().Should().BeGreaterThan(0);
            consumer.GetProperty("denominatorRule").GetString().Should().NotBeNullOrWhiteSpace();
            consumer.GetProperty("mustNotCount").GetArrayLength().Should().BeGreaterThan(0);
        }
    }

    private static JsonDocument ReadRoster(string root)
        => ReadJson(root, "docs", "gis", "data", "client-certification-roster.v1.json");

    private static JsonDocument ReadMatrix(string root)
        => ReadJson(root, "docs", "gis", "data", "client-certification-matrix.v1.json");

    private static JsonElement[] Entries(JsonElement document)
        => document.GetProperty("entries").EnumerateArray().ToArray();

    private static string Id(JsonElement entry) => entry.GetProperty("id").GetString()!;

    private static HashSet<string> IdsWithStatus(IEnumerable<JsonElement> entries, string status)
        => entries.Where(entry => string.Equals(entry.GetProperty("status").GetString(), status, StringComparison.Ordinal))
            .Select(Id).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> StringSet(JsonElement vocabulary, string property)
        => vocabulary.GetProperty(property).EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()!)
            .ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Families(JsonElement entry, string property)
        => entry.GetProperty("operationApplicability").GetProperty(property).EnumerateArray()
            .Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> NotApplicableFamilies(JsonElement entry)
        => entry.GetProperty("operationApplicability").GetProperty("notApplicable").EnumerateArray()
            .Select(item => item.GetProperty("family").GetString()!).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> Protocols(JsonElement entry)
        => entry.GetProperty("protocolSurfaces").EnumerateArray()
            .Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// The operation families a matrix lane declares <b>entirely</b> not-applicable.
    /// </summary>
    /// <remarks>
    /// Expands the lane's <c>notApplicable</c> patterns against the declared case ids and
    /// keeps only those families for which no case is left applicable. This is what makes
    /// the roster's family-level classification joinable to the matrix's id-level one
    /// without flattening a partially applicable family into a blanket exclusion.
    /// </remarks>
    private static HashSet<string> WhollyNotApplicableFamilies(
        JsonElement lane,
        IReadOnlyCollection<string> declaredCaseIds)
    {
        var notApplicable = lane.TryGetProperty("notApplicable", out var na)
            ? ExpandCasePatterns(na, declaredCaseIds)
            : new HashSet<string>(StringComparer.Ordinal);
        if (notApplicable.Count == 0)
        {
            return new HashSet<string>(StringComparer.Ordinal);
        }

        var families = new HashSet<string>(StringComparer.Ordinal);
        foreach (var family in notApplicable.Select(CaseFamilyOf).Distinct(StringComparer.Ordinal))
        {
            var declaredInFamily = declaredCaseIds
                .Where(id => string.Equals(CaseFamilyOf(id), family, StringComparison.Ordinal))
                .ToArray();
            if (declaredInFamily.Length > 0 && declaredInFamily.All(notApplicable.Contains))
            {
                families.Add(family);
            }
        }

        return families;
    }

    private static HashSet<string> ExpandCasePatterns(
        JsonElement patterns,
        IReadOnlyCollection<string> declaredCaseIds)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in patterns.EnumerateArray())
        {
            var pattern = element.GetString()!;
            if (pattern.EndsWith('*'))
            {
                var prefix = pattern[..^1];
                foreach (var id in declaredCaseIds.Where(id => id.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    expanded.Add(id);
                }
            }
            else
            {
                expanded.Add(pattern);
            }
        }

        return expanded;
    }

    /// <summary>
    /// The operation family of a concrete case id — <c>CERT-RNDR-SYM-01</c> is
    /// <c>CERT-RNDR</c>, as is <c>CERT-RNDR-01</c>.
    /// </summary>
    private static string CaseFamilyOf(string caseId)
    {
        var parts = caseId.Split('-');
        return parts.Length >= 2 ? string.Join('-', parts[0], parts[1]) : caseId;
    }

    private static string FamilyOf(string pattern)
        => pattern.EndsWith("-*", StringComparison.Ordinal) ? pattern[..^2] : pattern.TrimEnd('*');

    private static Dictionary<string, HashSet<string>> ReadEvidenceProtocols(string root)
    {
        var evidenceRoot = ArchitectureTestHelpers.CombinePath(root, "tests", "baselines", "client-compat");
        var result = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var path in Directory.EnumerateFiles(evidenceRoot, "*.cert.json", SearchOption.AllDirectories))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var lane = document.RootElement.GetProperty("client_lane").GetString()!;
            var protocol = document.RootElement.GetProperty("protocol").GetString()!;
            if (!result.TryGetValue(lane, out var protocols))
            {
                protocols = new HashSet<string>(StringComparer.Ordinal);
                result.Add(lane, protocols);
            }

            protocols.Add(protocol);
        }

        return result;
    }

    private static JsonDocument ReadJson(string root, params string[] relativeSegments)
        => JsonDocument.Parse(File.ReadAllText(ArchitectureTestHelpers.CombinePath([root, .. relativeSegments])));
}
