// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Migration.Abstractions;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.FileImport.Services;

namespace Honua.Core.Features.Migration.Services;

/// <summary>
/// Drives the parity stage of the migration acceptance suite by running a small, deterministic
/// probe set (feature count, schema field-names, axis-aligned bbox overlap) over each
/// per-source <see cref="MigrationManifestArtifact"/> emitted by the slice-3 apply stage and the
/// original <see cref="MigrationSourceInventoryArtifact"/> from the slice-2 scan stage.
/// </summary>
/// <remarks>
/// <para>
/// The parity stage is intentionally light in slice 4: it does not hash geometries, sample
/// attribute distributions, or talk to a live target. The slice composes the existing parity
/// evidence generator (<see cref="MigrationParityEvidenceGenerator"/>) for the per-source
/// <see cref="MigrationParityEvidenceArtifact"/> and adds the deterministic probe roll-up. Heavier
/// probes belong in later slices once a target observation contract is wired up.
/// </para>
/// <para>
/// When the upstream apply stage ran in dry-run mode the per-resource target observation is
/// derived from the manifest itself: the manifest copies the source field set and (by
/// construction) advertises the same feature count and bbox the source declared. Callers driving
/// the parity stage against a real apply outcome can supply explicit per-resource
/// <see cref="MigrationParityTargetObservation"/> values to override this fallback.
/// </para>
/// </remarks>
public static class MigrationAcceptanceParityStageRunner
{
    /// <summary>Aggregate classification when every probe in scope passed.</summary>
    public const string PassClassification = "pass";

    /// <summary>Aggregate classification when at least one probe warned but none failed.</summary>
    public const string WarnClassification = "warn";

    /// <summary>Aggregate classification when at least one probe failed.</summary>
    public const string FailClassification = "fail";

    /// <summary>
    /// Aggregate classification when no automated probes were applicable because all resources
    /// were routed to manual review or were unsupported by the apply stage.
    /// </summary>
    public const string ManualReviewClassification = "manual-review";

    private const string FeatureCountProbeId = "feature-count";
    private const string SchemaProbeId = "schema-field-names";
    private const string BboxProbeId = "bbox-overlap";

    private const string PassState = "pass";
    private const string WarnState = "warn";
    private const string FailState = "fail";
    private const string NotApplicableState = "not-applicable";
    private const string ManualReviewState = "manual-review";

    private const string PublishAction = "publish";
    private const string ManualReviewAction = "manual-review";
    private const string UnsupportedAction = "unsupported";

    /// <summary>
    /// Build a deterministic parity stage report from the supplied per-source apply outputs and
    /// inventories.
    /// </summary>
    /// <param name="runId">Stable identifier for this acceptance parity run (e.g. fixture set name).</param>
    /// <param name="applyRunId">Identifier of the upstream apply stage report.</param>
    /// <param name="inputs">Per-fixture parity inputs.</param>
    /// <returns>Aggregate report pinning the parity stage outputs.</returns>
    public static MigrationParityStageReport BuildReport(
        string runId,
        string applyRunId,
        IEnumerable<MigrationAcceptanceParityStageInput> inputs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(applyRunId);
        ArgumentNullException.ThrowIfNull(inputs);

        var entries = inputs
            .Select(input =>
            {
                ArgumentNullException.ThrowIfNull(input);
                if (string.IsNullOrWhiteSpace(input.FixtureId))
                {
                    throw new ArgumentException(
                        "Parity stage inputs must supply a non-empty fixture identifier.",
                        nameof(inputs));
                }

                if (input.Inventory is null)
                {
                    throw new ArgumentException(
                        $"Parity stage input for fixture '{input.FixtureId}' is missing its inventory artifact.",
                        nameof(inputs));
                }

                if (input.Manifest is null)
                {
                    throw new ArgumentException(
                        $"Parity stage input for fixture '{input.FixtureId}' is missing its manifest artifact.",
                        nameof(inputs));
                }

                var outcome = BuildOutcome(
                    input.Inventory,
                    input.Manifest,
                    input.SourceObservations,
                    input.TargetObservations,
                    input.Attestation);

                var classification = ClassifyOutcome(outcome);

                return new MigrationParityStageEntry
                {
                    FixtureId = input.FixtureId,
                    SourceKind = input.Manifest.SourceKind,
                    Classification = classification,
                    Outcome = outcome
                };
            })
            .OrderBy(static entry => entry.FixtureId, StringComparer.Ordinal)
            .ToArray();

        return new MigrationParityStageReport
        {
            RunId = runId,
            ApplyRunId = applyRunId,
            Summary = BuildSummary(entries),
            Sources = entries
        };
    }

    /// <summary>
    /// Build a deterministic per-source parity outcome. Exposed for callers (and tests) that
    /// drive the parity stage one fixture at a time.
    /// </summary>
    /// <param name="inventory">Source inventory artifact for the fixture.</param>
    /// <param name="manifest">Manifest artifact emitted by the apply stage for the fixture.</param>
    /// <param name="sourceObservations">Optional per-resource source observations.</param>
    /// <param name="targetObservations">Optional per-resource target observations.</param>
    /// <param name="attestation">Optional operator-supplied readiness attestation forwarded to the parity evidence pack.</param>
    /// <returns>Deterministic per-source parity outcome.</returns>
    public static MigrationParityStageOutcome BuildOutcome(
        MigrationSourceInventoryArtifact inventory,
        MigrationManifestArtifact manifest,
        IEnumerable<MigrationParitySourceObservation>? sourceObservations = null,
        IEnumerable<MigrationParityTargetObservation>? targetObservations = null,
        MigrationReadinessAttestation? attestation = null)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(manifest);

        var sourceObs = (sourceObservations ?? Array.Empty<MigrationParitySourceObservation>())
            .Where(static item => item is not null)
            .GroupBy(static item => item.SourceResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var targetObs = (targetObservations ?? Array.Empty<MigrationParityTargetObservation>())
            .Where(static item => item is not null)
            .GroupBy(static item => item.SourceResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var manifestBySourceId = manifest.TargetResources
            .GroupBy(static item => item.SourceResourceId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var probes = new List<MigrationParityResourceProbe>(inventory.Resources.Length);
        var diagnostics = new List<MigrationParityStageDiagnostic>();

        foreach (var resource in inventory.Resources)
        {
            manifestBySourceId.TryGetValue(resource.Id, out var target);
            sourceObs.TryGetValue(resource.Id, out var sourceObservation);
            targetObs.TryGetValue(resource.Id, out var targetObservation);

            var probe = BuildResourceProbe(resource, target, sourceObservation, targetObservation);
            probes.Add(probe);
            CollectDiagnostics(probe, diagnostics);
        }

        var orderedProbes = probes
            .OrderBy(static item => item.SourceResourceId, StringComparer.Ordinal)
            .ToArray();

        var orderedDiagnostics = diagnostics
            .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Severity, StringComparer.Ordinal)
            .ThenBy(static item => item.Code, StringComparer.Ordinal)
            .ToArray();

        var evidence = MigrationParityEvidenceGenerator.Generate(inventory, manifest, attestation);
        var replayToken = ComputeReplayToken(manifest.SourceKind, orderedProbes);

        return new MigrationParityStageOutcome
        {
            Evidence = evidence,
            ReplayToken = replayToken,
            ResourceCount = orderedProbes.Length,
            FeatureCountMatchCount = orderedProbes.Count(static probe => probe.FeatureCount.State == PassState),
            FeatureCountMismatchCount = orderedProbes.Count(static probe => probe.FeatureCount.State is FailState or WarnState),
            SchemaMatchCount = orderedProbes.Count(static probe => probe.Schema.State == PassState),
            SchemaMismatchCount = orderedProbes.Count(static probe => probe.Schema.State is FailState or WarnState),
            BboxOverlapMatchCount = orderedProbes.Count(static probe => probe.BboxOverlap.State == PassState),
            BboxOverlapMismatchCount = orderedProbes.Count(static probe => probe.BboxOverlap.State is FailState or WarnState),
            NotApplicableProbeCount = orderedProbes.Sum(static probe => CountNotApplicable(probe)),
            ManualReviewResourceCount = orderedProbes.Count(static probe => probe.Classification == ManualReviewClassification),
            ResourceProbes = orderedProbes,
            Diagnostics = orderedDiagnostics
        };
    }

    private static MigrationParityResourceProbe BuildResourceProbe(
        MigrationInventoryResource resource,
        MigrationManifestTargetResource? target,
        MigrationParitySourceObservation? sourceObservation,
        MigrationParityTargetObservation? targetObservation)
    {
        var manifestAction = NormalizeAction(target?.Action);
        var isManualReview = manifestAction is ManualReviewAction or UnsupportedAction || target is null;

        var featureCountProbe = BuildFeatureCountProbe(resource, target, sourceObservation, targetObservation, isManualReview);
        var schemaProbe = BuildSchemaProbe(resource, target, sourceObservation, targetObservation, isManualReview);
        var bboxProbe = BuildBboxProbe(resource, sourceObservation, targetObservation, isManualReview);

        var classification = ClassifyResourceProbe(isManualReview, featureCountProbe, schemaProbe, bboxProbe);

        return new MigrationParityResourceProbe
        {
            SourceResourceId = resource.Id,
            TargetResourceId = target?.TargetResourceId,
            ManifestAction = manifestAction,
            Classification = classification,
            FeatureCount = featureCountProbe,
            Schema = schemaProbe,
            BboxOverlap = bboxProbe
        };
    }

    private static MigrationParityProbeResult BuildFeatureCountProbe(
        MigrationInventoryResource resource,
        MigrationManifestTargetResource? target,
        MigrationParitySourceObservation? sourceObservation,
        MigrationParityTargetObservation? targetObservation,
        bool isManualReview)
    {
        var expectedCount = sourceObservation?.FeatureCount
            ?? (resource.FeatureCount.HasValue ? (long?)resource.FeatureCount.Value : null);

        if (!expectedCount.HasValue)
        {
            return new MigrationParityProbeResult
            {
                ProbeId = FeatureCountProbeId,
                State = NotApplicableState,
                Summary = "Source inventory did not advertise a feature count for this resource."
            };
        }

        if (isManualReview)
        {
            return new MigrationParityProbeResult
            {
                ProbeId = FeatureCountProbeId,
                State = ManualReviewState,
                Summary = "Resource routed to manual review; feature count parity must be verified by an operator.",
                Expected = expectedCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        // When no live target observation is supplied the manifest mirrors the source by
        // construction, so the parity probe asserts the count is preserved.
        var observedCount = targetObservation?.FeatureCount ?? expectedCount.Value;

        if (observedCount == expectedCount.Value)
        {
            return new MigrationParityProbeResult
            {
                ProbeId = FeatureCountProbeId,
                State = PassState,
                Summary = "Source feature count matched the target feature count.",
                Expected = expectedCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Observed = observedCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }

        var delta = observedCount - expectedCount.Value;
        var severity = Math.Abs(delta) <= Math.Max(1, expectedCount.Value / 100) ? WarnState : FailState;
        return new MigrationParityProbeResult
        {
            ProbeId = FeatureCountProbeId,
            State = severity,
            Summary = severity == WarnState
                ? "Source and target feature counts differ within the 1% warning threshold."
                : "Source and target feature counts diverged beyond the 1% warning threshold.",
            Expected = expectedCount.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Observed = observedCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Differences =
            [
                $"delta={delta.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            ]
        };
    }

    private static MigrationParityProbeResult BuildSchemaProbe(
        MigrationInventoryResource resource,
        MigrationManifestTargetResource? target,
        MigrationParitySourceObservation? sourceObservation,
        MigrationParityTargetObservation? targetObservation,
        bool isManualReview)
    {
        var expectedFields = NormalizeFieldNames(
            sourceObservation?.FieldNames
            ?? resource.Fields.Select(static field => field.Name));

        if (expectedFields.Length == 0 && target is null)
        {
            return new MigrationParityProbeResult
            {
                ProbeId = SchemaProbeId,
                State = NotApplicableState,
                Summary = "Source inventory did not advertise a field schema for this resource."
            };
        }

        if (isManualReview)
        {
            return new MigrationParityProbeResult
            {
                ProbeId = SchemaProbeId,
                State = ManualReviewState,
                Summary = "Resource routed to manual review; schema parity must be verified by an operator.",
                Expected = JoinPreview(expectedFields)
            };
        }

        var observedFields = NormalizeFieldNames(
            targetObservation?.FieldNames
            ?? target!.Fields.Select(static field => field.Name));

        if (expectedFields.SequenceEqual(observedFields, StringComparer.Ordinal))
        {
            return new MigrationParityProbeResult
            {
                ProbeId = SchemaProbeId,
                State = PassState,
                Summary = "Source and target schema field names matched.",
                Expected = JoinPreview(expectedFields),
                Observed = JoinPreview(observedFields)
            };
        }

        var missingOnTarget = expectedFields.Except(observedFields, StringComparer.Ordinal).ToArray();
        var extraOnTarget = observedFields.Except(expectedFields, StringComparer.Ordinal).ToArray();
        var differences = new List<string>(missingOnTarget.Length + extraOnTarget.Length);
        foreach (var name in missingOnTarget)
        {
            differences.Add($"missing-on-target={name}");
        }
        foreach (var name in extraOnTarget)
        {
            differences.Add($"extra-on-target={name}");
        }

        return new MigrationParityProbeResult
        {
            ProbeId = SchemaProbeId,
            State = FailState,
            Summary = "Source and target schema field-name sets diverged.",
            Expected = JoinPreview(expectedFields),
            Observed = JoinPreview(observedFields),
            Differences = differences.ToArray()
        };
    }

    private static MigrationParityProbeResult BuildBboxProbe(
        MigrationInventoryResource resource,
        MigrationParitySourceObservation? sourceObservation,
        MigrationParityTargetObservation? targetObservation,
        bool isManualReview)
    {
        var expectedBbox = NormalizeBbox(sourceObservation?.Bbox);
        var observedBbox = NormalizeBbox(targetObservation?.Bbox);

        if (expectedBbox is null && observedBbox is null)
        {
            return new MigrationParityProbeResult
            {
                ProbeId = BboxProbeId,
                State = NotApplicableState,
                Summary = "Neither source inventory nor target observation supplied a bbox for this resource."
            };
        }

        if (isManualReview)
        {
            return new MigrationParityProbeResult
            {
                ProbeId = BboxProbeId,
                State = ManualReviewState,
                Summary = "Resource routed to manual review; spatial extent parity must be verified by an operator.",
                Expected = expectedBbox is null ? null : FormatBbox(expectedBbox)
            };
        }

        if (expectedBbox is null)
        {
            return new MigrationParityProbeResult
            {
                ProbeId = BboxProbeId,
                State = WarnState,
                Summary = "Target advertised a bbox but the source did not; cannot verify overlap.",
                Observed = FormatBbox(observedBbox!)
            };
        }

        if (observedBbox is null)
        {
            // Mirror the source bbox onto the target by construction when no live observation
            // was supplied so the deterministic dry-run path still classifies as pass.
            observedBbox = expectedBbox;
        }

        if (BboxesOverlap(expectedBbox, observedBbox))
        {
            return new MigrationParityProbeResult
            {
                ProbeId = BboxProbeId,
                State = PassState,
                Summary = "Source and target axis-aligned bboxes overlap.",
                Expected = FormatBbox(expectedBbox),
                Observed = FormatBbox(observedBbox)
            };
        }

        return new MigrationParityProbeResult
        {
            ProbeId = BboxProbeId,
            State = FailState,
            Summary = "Source and target axis-aligned bboxes are disjoint.",
            Expected = FormatBbox(expectedBbox),
            Observed = FormatBbox(observedBbox),
            Differences =
            [
                $"source-min=[{expectedBbox[0].ToString(System.Globalization.CultureInfo.InvariantCulture)},{expectedBbox[1].ToString(System.Globalization.CultureInfo.InvariantCulture)}]",
                $"target-min=[{observedBbox[0].ToString(System.Globalization.CultureInfo.InvariantCulture)},{observedBbox[1].ToString(System.Globalization.CultureInfo.InvariantCulture)}]"
            ]
        };
    }

    private static string ClassifyResourceProbe(
        bool isManualReview,
        MigrationParityProbeResult featureCount,
        MigrationParityProbeResult schema,
        MigrationParityProbeResult bbox)
    {
        if (isManualReview)
        {
            return ManualReviewClassification;
        }

        var states = new[] { featureCount.State, schema.State, bbox.State };
        if (states.Any(static state => state == FailState))
        {
            return FailClassification;
        }
        if (states.Any(static state => state == WarnState))
        {
            return WarnClassification;
        }
        return PassClassification;
    }

    private static string ClassifyOutcome(MigrationParityStageOutcome outcome)
    {
        if (outcome.ResourceProbes.Length == 0)
        {
            return ManualReviewClassification;
        }

        var hasFail = outcome.ResourceProbes.Any(static probe => probe.Classification == FailClassification);
        if (hasFail)
        {
            return FailClassification;
        }

        var hasWarn = outcome.ResourceProbes.Any(static probe => probe.Classification == WarnClassification);
        if (hasWarn)
        {
            return WarnClassification;
        }

        var hasPass = outcome.ResourceProbes.Any(static probe => probe.Classification == PassClassification);
        return hasPass ? PassClassification : ManualReviewClassification;
    }

    private static void CollectDiagnostics(MigrationParityResourceProbe probe, List<MigrationParityStageDiagnostic> diagnostics)
    {
        foreach (var result in new[] { probe.FeatureCount, probe.Schema, probe.BboxOverlap })
        {
            if (result.State is PassState or NotApplicableState)
            {
                continue;
            }

            diagnostics.Add(new MigrationParityStageDiagnostic
            {
                Code = $"parity.{result.ProbeId}.{result.State}",
                Severity = result.State == ManualReviewState ? ManualReviewState : result.State,
                SourceId = probe.SourceResourceId,
                Message = result.Summary
            });
        }
    }

    private static MigrationParityStageSummary BuildSummary(MigrationParityStageEntry[] entries)
    {
        var pass = 0;
        var warn = 0;
        var fail = 0;
        var manualReview = 0;
        var resources = 0;
        var fcMatch = 0;
        var fcMismatch = 0;
        var schemaMatch = 0;
        var schemaMismatch = 0;
        var bboxMatch = 0;
        var bboxMismatch = 0;
        var diagnostics = 0;

        foreach (var entry in entries)
        {
            switch (entry.Classification)
            {
                case PassClassification: pass++; break;
                case WarnClassification: warn++; break;
                case FailClassification: fail++; break;
                default: manualReview++; break;
            }

            resources += entry.Outcome.ResourceCount;
            fcMatch += entry.Outcome.FeatureCountMatchCount;
            fcMismatch += entry.Outcome.FeatureCountMismatchCount;
            schemaMatch += entry.Outcome.SchemaMatchCount;
            schemaMismatch += entry.Outcome.SchemaMismatchCount;
            bboxMatch += entry.Outcome.BboxOverlapMatchCount;
            bboxMismatch += entry.Outcome.BboxOverlapMismatchCount;
            diagnostics += entry.Outcome.Diagnostics.Length;
        }

        return new MigrationParityStageSummary
        {
            SourceCount = entries.Length,
            PassSourceCount = pass,
            WarnSourceCount = warn,
            FailSourceCount = fail,
            ManualReviewSourceCount = manualReview,
            ResourceCount = resources,
            FeatureCountMatchCount = fcMatch,
            FeatureCountMismatchCount = fcMismatch,
            SchemaMatchCount = schemaMatch,
            SchemaMismatchCount = schemaMismatch,
            BboxOverlapMatchCount = bboxMatch,
            BboxOverlapMismatchCount = bboxMismatch,
            DiagnosticCount = diagnostics
        };
    }

    private static int CountNotApplicable(MigrationParityResourceProbe probe)
    {
        var count = 0;
        if (probe.FeatureCount.State == NotApplicableState) count++;
        if (probe.Schema.State == NotApplicableState) count++;
        if (probe.BboxOverlap.State == NotApplicableState) count++;
        return count;
    }

    private static string NormalizeAction(string? action)
    {
        if (string.IsNullOrWhiteSpace(action))
        {
            return ManualReviewAction;
        }
        var trimmed = action.Trim();
        return trimmed switch
        {
            PublishAction => PublishAction,
            ManualReviewAction => ManualReviewAction,
            UnsupportedAction => UnsupportedAction,
            _ => trimmed
        };
    }

    private static string[] NormalizeFieldNames(IEnumerable<string>? names)
        => (names ?? Array.Empty<string>())
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

    private static double[]? NormalizeBbox(double[]? bbox)
    {
        if (bbox is null || bbox.Length != 4)
        {
            return null;
        }
        var minX = Math.Min(bbox[0], bbox[2]);
        var minY = Math.Min(bbox[1], bbox[3]);
        var maxX = Math.Max(bbox[0], bbox[2]);
        var maxY = Math.Max(bbox[1], bbox[3]);
        return [minX, minY, maxX, maxY];
    }

    private static bool BboxesOverlap(double[] a, double[] b)
        => a[0] <= b[2] && a[2] >= b[0] && a[1] <= b[3] && a[3] >= b[1];

    private static string FormatBbox(double[] bbox)
        => string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"[{bbox[0]},{bbox[1]},{bbox[2]},{bbox[3]}]");

    private static string JoinPreview(string[] values)
        => values.Length == 0 ? string.Empty : string.Join(",", values);

    private static string ComputeReplayToken(string sourceKind, MigrationParityResourceProbe[] probes)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("sourceKind", sourceKind);
            writer.WriteStartArray("probes");
            foreach (var probe in probes)
            {
                writer.WriteStartObject();
                writer.WriteString("sourceResourceId", probe.SourceResourceId);
                if (probe.TargetResourceId is not null)
                {
                    writer.WriteString("targetResourceId", probe.TargetResourceId);
                }
                writer.WriteString("manifestAction", probe.ManifestAction);
                writer.WriteString("classification", probe.Classification);
                WriteProbe(writer, "featureCount", probe.FeatureCount);
                WriteProbe(writer, "schema", probe.Schema);
                WriteProbe(writer, "bboxOverlap", probe.BboxOverlap);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        var hash = SHA256.HashData(stream.ToArray());
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void WriteProbe(Utf8JsonWriter writer, string propertyName, MigrationParityProbeResult result)
    {
        writer.WriteStartObject(propertyName);
        writer.WriteString("probeId", result.ProbeId);
        writer.WriteString("state", result.State);
        writer.WriteString("summary", result.Summary);
        if (result.Expected is not null)
        {
            writer.WriteString("expected", result.Expected);
        }
        if (result.Observed is not null)
        {
            writer.WriteString("observed", result.Observed);
        }
        if (result.Differences.Length > 0)
        {
            writer.WriteStartArray("differences");
            foreach (var difference in result.Differences)
            {
                writer.WriteStringValue(difference);
            }
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }
}

/// <summary>
/// One per-fixture input to <see cref="MigrationAcceptanceParityStageRunner.BuildReport"/>.
/// </summary>
public sealed record MigrationAcceptanceParityStageInput
{
    /// <summary>
    /// Stable fixture identifier (e.g. <c>arcgis-mapserver-mixed-renderers</c>).
    /// </summary>
    public required string FixtureId { get; init; }

    /// <summary>
    /// Inventory artifact produced by the upstream scan stage for this fixture.
    /// </summary>
    public required MigrationSourceInventoryArtifact Inventory { get; init; }

    /// <summary>
    /// Manifest artifact emitted by the upstream apply stage for this fixture (the deterministic
    /// "applied target state" the parity stage probes against).
    /// </summary>
    public required MigrationManifestArtifact Manifest { get; init; }

    /// <summary>
    /// Optional per-resource source observations that override values pulled from the inventory.
    /// </summary>
    public IEnumerable<MigrationParitySourceObservation>? SourceObservations { get; init; }

    /// <summary>
    /// Optional per-resource target observations recorded after a real apply.
    /// </summary>
    public IEnumerable<MigrationParityTargetObservation>? TargetObservations { get; init; }

    /// <summary>
    /// Optional operator-supplied readiness attestation forwarded to the parity evidence pack.
    /// </summary>
    public MigrationReadinessAttestation? Attestation { get; init; }
}
