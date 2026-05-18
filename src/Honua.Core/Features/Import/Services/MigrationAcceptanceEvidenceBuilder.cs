// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Builds release-gate migration acceptance evidence from source-specific artifacts.
/// </summary>
public static class MigrationAcceptanceEvidenceBuilder
{
    /// <summary>
    /// Builds a deterministic acceptance-suite artifact from source inventory, manifest, and parity evidence inputs.
    /// </summary>
    /// <param name="runId">Stable workflow or release run identifier.</param>
    /// <param name="inputs">Per-source evidence inputs.</param>
    /// <param name="options">Optional suite gate options.</param>
    /// <returns>Migration acceptance evidence suite artifact.</returns>
    public static MigrationAcceptanceEvidenceArtifact Build(
        string runId,
        IEnumerable<MigrationAcceptanceEvidenceInput> inputs,
        MigrationAcceptanceEvidenceOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(inputs);

        options ??= new MigrationAcceptanceEvidenceOptions();

        var entries = inputs
            .Select(input => BuildEntry(input, options))
            .OrderBy(static entry => entry.SourceKind, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Id, StringComparer.Ordinal)
            .ToArray();
        var gaps = BuildBlockingGaps(entries, options).ToArray();
        var summary = BuildSummary(entries, gaps, options);

        return new MigrationAcceptanceEvidenceArtifact
        {
            RunId = runId.Trim(),
            Summary = summary,
            Entries = entries,
            BlockingGaps = gaps
        };
    }

    private static MigrationAcceptanceEvidenceEntry BuildEntry(
        MigrationAcceptanceEvidenceInput input,
        MigrationAcceptanceEvidenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(input.Inventory);

        var manifest = input.Manifest ?? MigrationManifestTranslator.Translate(input.Inventory);
        var parityEvidence = input.ParityEvidence ?? MigrationParityEvidenceGenerator.Generate(
            input.Inventory,
            manifest,
            input.ReadinessAttestation,
            input.PerformanceCost);
        var stages = BuildStages(
            input,
            manifest,
            parityEvidence,
            suppliedManifest: input.Manifest != null,
            suppliedParityEvidence: input.ParityEvidence != null);
        var manualReviewCount = manifest.ManualReviewItems.Length + manifest.StyleActions.Count(static action =>
            string.Equals(action.Action, "manual-review", StringComparison.OrdinalIgnoreCase));
        var unsupportedCount = manifest.UnsupportedItems.Length;

        return new MigrationAcceptanceEvidenceEntry
        {
            Id = string.IsNullOrWhiteSpace(input.Id) ? input.Inventory.SourceKind : input.Id.Trim(),
            SourceKind = input.Inventory.SourceKind,
            Source = input.Inventory.Source,
            State = AggregateState(stages.Select(static stage => stage.State)),
            AutomationLevel = ClassifyAutomation(manifest, parityEvidence, manualReviewCount, unsupportedCount),
            Stages = stages,
            ManualReviewCount = manualReviewCount,
            UnsupportedCount = unsupportedCount,
            ManifestAvailable = parityEvidence.ManifestAvailable,
            InventoryArtifactKind = input.Inventory.ArtifactKind,
            ManifestArtifactKind = manifest.ArtifactKind,
            ParityEvidenceArtifactKind = parityEvidence.ArtifactKind,
            EvidenceReferences = SanitizeEvidenceReferences(input.EvidenceReferences),
            Notes = BuildEntryNotes(manifest, parityEvidence, options)
        };
    }

    private static IEnumerable<MigrationAcceptanceEvidenceGap> BuildBlockingGaps(
        IReadOnlyCollection<MigrationAcceptanceEvidenceEntry> entries,
        MigrationAcceptanceEvidenceOptions options)
    {
        var coveredSourceKinds = entries
            .Select(static entry => entry.SourceKind)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var sourceKind in Order(options.RequiredSourceKinds))
        {
            if (!coveredSourceKinds.Contains(sourceKind))
            {
                yield return new MigrationAcceptanceEvidenceGap
                {
                    Id = $"missing-source-kind:{sourceKind}",
                    SourceKind = sourceKind,
                    State = MigrationEvidenceStates.Fail,
                    Summary = $"Required migration source kind '{sourceKind}' has no evidence entry.",
                    Remediation = ["Add a deterministic fixture or scheduled evidence lane for this source kind."]
                };
            }
        }

        if (options.RequireAutomatedEntries)
        {
            foreach (var entry in entries.Where(static entry =>
                         entry.AutomationLevel != MigrationAutomationLevels.Automated))
            {
                yield return new MigrationAcceptanceEvidenceGap
                {
                    Id = $"automation-level:{entry.Id}",
                    SourceKind = entry.SourceKind,
                    State = entry.AutomationLevel == MigrationAutomationLevels.Unsupported
                        ? MigrationEvidenceStates.Fail
                        : MigrationEvidenceStates.Unknown,
                    Summary = $"{entry.Id} is classified as {entry.AutomationLevel}.",
                    Remediation = ["Close unsupported items or record an explicit assisted-migration claim instead of automated migration."]
                };
            }
        }

        foreach (var entry in entries)
        {
            foreach (var stage in entry.Stages.Where(static stage =>
                         stage.State is MigrationEvidenceStates.Fail or MigrationEvidenceStates.Unknown))
            {
                yield return new MigrationAcceptanceEvidenceGap
                {
                    Id = $"stage:{entry.Id}:{stage.Id}",
                    SourceKind = entry.SourceKind,
                    State = stage.State,
                    Summary = $"{entry.Id} {stage.Id} stage is {stage.State}.",
                    Remediation = stage.Id switch
                    {
                        MigrationAcceptanceStageIds.ApplyOrDryRun =>
                            ["Attach apply or dry-run evidence before claiming end-to-end automated migration."],
                        MigrationAcceptanceStageIds.Publish =>
                            ["Attach publish evidence before claiming published target parity."],
                        MigrationAcceptanceStageIds.Readiness =>
                            ["Complete or waive cutover-readiness attestations before release evidence is accepted."],
                        _ => ["Attach passing evidence for this stage or classify the migration as assisted/manual."]
                    }
                };
            }
        }
    }

    private static MigrationAcceptanceEvidenceSummary BuildSummary(
        MigrationAcceptanceEvidenceEntry[] entries,
        MigrationAcceptanceEvidenceGap[] gaps,
        MigrationAcceptanceEvidenceOptions options)
    {
        var states = entries.Select(static entry => entry.State).Concat(gaps.Select(static gap => gap.State));
        var overallState = AggregateState(states);

        return new MigrationAcceptanceEvidenceSummary
        {
            OverallState = overallState,
            SourceCount = entries.Length,
            PassingSourceCount = entries.Count(static entry => entry.State == MigrationEvidenceStates.Pass),
            FailingSourceCount = entries.Count(static entry => entry.State == MigrationEvidenceStates.Fail),
            UnknownSourceCount = entries.Count(static entry => entry.State == MigrationEvidenceStates.Unknown),
            AutomatedSourceCount = entries.Count(static entry => entry.AutomationLevel == MigrationAutomationLevels.Automated),
            ManualReviewSourceCount = entries.Count(static entry =>
                entry.AutomationLevel is MigrationAutomationLevels.Assisted or MigrationAutomationLevels.ManualReview),
            UnsupportedSourceCount = entries.Count(static entry => entry.AutomationLevel == MigrationAutomationLevels.Unsupported),
            RequiredSourceKinds = Order(options.RequiredSourceKinds),
            CoveredSourceKinds = entries
                .Select(static entry => entry.SourceKind)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static sourceKind => sourceKind, StringComparer.Ordinal)
                .ToArray()
        };
    }

    private static string ClassifyAutomation(
        MigrationManifestArtifact manifest,
        MigrationParityEvidenceArtifact parityEvidence,
        int manualReviewCount,
        int unsupportedCount)
    {
        if (unsupportedCount > 0 || parityEvidence.Sections.SelectMany(static section => section.Items)
                .Any(static item => item.State == MigrationEvidenceStates.Fail))
        {
            return MigrationAutomationLevels.Unsupported;
        }

        if (manualReviewCount == 0)
        {
            return MigrationAutomationLevels.Automated;
        }

        return manualReviewCount >= manifest.TargetResources.Length
            ? MigrationAutomationLevels.ManualReview
            : MigrationAutomationLevels.Assisted;
    }

    private static MigrationAcceptanceEvidenceStage[] BuildStages(
        MigrationAcceptanceEvidenceInput input,
        MigrationManifestArtifact manifest,
        MigrationParityEvidenceArtifact parityEvidence,
        bool suppliedManifest,
        bool suppliedParityEvidence)
    {
        var overrides = input.StageEvidence
            .Where(static stage => !string.IsNullOrWhiteSpace(stage.Id))
            .GroupBy(static stage => stage.Id.Trim(), StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);

        return
        [
            BuildStage(
                MigrationAcceptanceStageIds.Scan,
                overrides,
                MigrationEvidenceStates.Pass,
                "Source inventory artifact is available.",
                [input.Inventory.ArtifactKind],
                []),
            BuildStage(
                MigrationAcceptanceStageIds.Manifest,
                overrides,
                MigrationEvidenceStates.Pass,
                suppliedManifest
                    ? "Migration manifest artifact was supplied."
                    : "Migration manifest artifact was generated from the inventory.",
                [manifest.ArtifactKind],
                suppliedManifest ? [] : ["Persist the generated manifest with release evidence for reviewer inspection."]),
            BuildStage(
                MigrationAcceptanceStageIds.ApplyOrDryRun,
                overrides,
                MigrationEvidenceStates.Unknown,
                "Apply or dry-run evidence was not supplied.",
                [],
                ["Run the bounded source-specific apply or dry-run lane and attach its evidence artifact."]),
            BuildStage(
                MigrationAcceptanceStageIds.Publish,
                overrides,
                MigrationEvidenceStates.Unknown,
                "Publish evidence was not supplied.",
                [],
                ["Attach target publish evidence before claiming published-service parity."]),
            BuildStage(
                MigrationAcceptanceStageIds.Parity,
                overrides,
                parityEvidence.OverallState,
                parityEvidence.Summary,
                [parityEvidence.ArtifactKind],
                suppliedParityEvidence ? [] : ["Persist the generated parity evidence pack with release evidence."]),
            BuildStage(
                MigrationAcceptanceStageIds.Readiness,
                overrides,
                parityEvidence.CutoverReadiness.State,
                $"Cutover readiness state is {parityEvidence.CutoverReadiness.State}.",
                [parityEvidence.ArtifactKind],
                [])
        ];
    }

    private static MigrationAcceptanceEvidenceStage BuildStage(
        string id,
        Dictionary<string, MigrationAcceptanceStageEvidenceInput> overrides,
        string defaultState,
        string defaultSummary,
        IEnumerable<string> defaultArtifactKinds,
        IEnumerable<string> defaultNotes)
    {
        if (overrides.TryGetValue(id, out var evidence))
        {
            return new MigrationAcceptanceEvidenceStage
            {
                Id = id,
                State = NormalizeState(evidence.State),
                Summary = string.IsNullOrWhiteSpace(evidence.Summary) ? defaultSummary : evidence.Summary.Trim(),
                ArtifactKinds = Order(evidence.ArtifactKinds),
                EvidenceReferences = SanitizeEvidenceReferences(evidence.EvidenceReferences),
                Notes = Order(evidence.Notes)
            };
        }

        return new MigrationAcceptanceEvidenceStage
        {
            Id = id,
            State = NormalizeState(defaultState),
            Summary = defaultSummary,
            ArtifactKinds = Order(defaultArtifactKinds),
            EvidenceReferences = [],
            Notes = Order(defaultNotes)
        };
    }

    private static string[] BuildEntryNotes(
        MigrationManifestArtifact manifest,
        MigrationParityEvidenceArtifact parityEvidence,
        MigrationAcceptanceEvidenceOptions options)
    {
        var notes = new List<string>();
        if (manifest.ManualReviewItems.Length > 0)
        {
            notes.Add($"{manifest.ManualReviewItems.Length} manifest item(s) require manual review.");
        }

        if (manifest.UnsupportedItems.Length > 0)
        {
            notes.Add($"{manifest.UnsupportedItems.Length} manifest item(s) are unsupported.");
        }

        if (parityEvidence.OverallState != MigrationEvidenceStates.Pass)
        {
            notes.Add($"Parity evidence state is {parityEvidence.OverallState}.");
        }

        if (options.RequireReadinessPass && parityEvidence.CutoverReadiness.State != MigrationEvidenceStates.Pass)
        {
            notes.Add($"Cutover readiness state is {parityEvidence.CutoverReadiness.State}.");
        }

        return Order(notes);
    }

    private static string AggregateState(IEnumerable<string> states)
    {
        var materialized = states.Select(NormalizeState).ToArray();
        if (materialized.Any(static state => state == MigrationEvidenceStates.Fail))
        {
            return MigrationEvidenceStates.Fail;
        }

        if (materialized.Length == 0 || materialized.Any(static state => state == MigrationEvidenceStates.Unknown))
        {
            return MigrationEvidenceStates.Unknown;
        }

        return MigrationEvidenceStates.Pass;
    }

    private static string NormalizeState(string state)
        => state switch
        {
            MigrationEvidenceStates.Pass => MigrationEvidenceStates.Pass,
            MigrationEvidenceStates.Fail => MigrationEvidenceStates.Fail,
            MigrationEvidenceStates.Unknown => MigrationEvidenceStates.Unknown,
            MigrationEvidenceStates.NotApplicable => MigrationEvidenceStates.NotApplicable,
            _ => MigrationEvidenceStates.Unknown
        };

    private static string[] SanitizeEvidenceReferences(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => SanitizeEvidenceReference(value.Trim()))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string SanitizeEvidenceReference(string value)
    {
        var sensitiveTailIndex = value.IndexOfAny(['?', '#']);
        var withoutQueryOrFragment = sensitiveTailIndex >= 0 ? value[..sensitiveTailIndex] : value;

        if (!Uri.TryCreate(withoutQueryOrFragment, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.UserInfo))
        {
            return withoutQueryOrFragment;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }

    private static string[] Order(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
}

/// <summary>
/// Per-source inputs used to build an acceptance-suite evidence entry.
/// </summary>
public sealed record MigrationAcceptanceEvidenceInput
{
    /// <summary>
    /// Stable source evidence entry identifier.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Source inventory artifact.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Optional migration manifest. When omitted, the builder translates the inventory.
    /// </summary>
    public MigrationManifestArtifact? Manifest { get; init; }

    /// <summary>
    /// Optional parity evidence. When omitted, the builder generates parity from the inventory and manifest.
    /// </summary>
    public MigrationParityEvidenceArtifact? ParityEvidence { get; init; }

    /// <summary>
    /// Optional readiness attestation used when parity evidence must be generated.
    /// </summary>
    public MigrationReadinessAttestation? ReadinessAttestation { get; init; }

    /// <summary>
    /// Optional performance and migration-cost evidence used when parity evidence must be generated.
    /// </summary>
    public MigrationPerformanceCostEvidence? PerformanceCost { get; init; }

    /// <summary>
    /// Optional externally collected evidence for canonical acceptance stages such as apply/dry-run and publish.
    /// </summary>
    public MigrationAcceptanceStageEvidenceInput[] StageEvidence { get; init; } = [];

    /// <summary>
    /// Secret-safe artifact references supporting this evidence entry.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];
}

/// <summary>
/// Externally collected evidence for one canonical migration acceptance stage.
/// </summary>
public sealed record MigrationAcceptanceStageEvidenceInput
{
    /// <summary>
    /// Stable stage identifier from <see cref="MigrationAcceptanceStageIds"/>.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Stage state: <c>pass</c>, <c>fail</c>, <c>unknown</c>, or <c>not-applicable</c>.
    /// </summary>
    public required string State { get; init; }

    /// <summary>
    /// Human-readable summary for this stage.
    /// </summary>
    public required string Summary { get; init; }

    /// <summary>
    /// Artifact kinds supporting this stage.
    /// </summary>
    public string[] ArtifactKinds { get; init; } = [];

    /// <summary>
    /// Secret-safe links or paths to supporting evidence artifacts for this stage.
    /// </summary>
    public string[] EvidenceReferences { get; init; } = [];

    /// <summary>
    /// Deterministic notes explaining stage limitations or follow-up work.
    /// </summary>
    public string[] Notes { get; init; } = [];
}

/// <summary>
/// Options for building a migration acceptance evidence suite.
/// </summary>
public sealed record MigrationAcceptanceEvidenceOptions
{
    /// <summary>
    /// Source kinds that must be represented for the suite to pass.
    /// </summary>
    public string[] RequiredSourceKinds { get; init; } = [];

    /// <summary>
    /// Whether non-automated entries should produce blocking gaps.
    /// </summary>
    public bool RequireAutomatedEntries { get; init; } = true;

    /// <summary>
    /// Whether non-passing readiness should be surfaced in entry notes.
    /// </summary>
    public bool RequireReadinessPass { get; init; } = true;
}
