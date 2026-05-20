// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Services;

/// <summary>
/// Runs deterministic post-migration parity probes against a Honua-applied target for an ArcGIS
/// source and emits an <see cref="ArcGisMigrationParityArtifact"/>. The runner is the slice-5
/// counterpart to slice 2-4 manifest evidence: where the manifest captures intent, this runner
/// captures what was actually realized on the target.
/// </summary>
/// <remarks>
/// The runner performs four families of probes per target resource:
/// <list type="bullet">
///   <item><description><b>Feature count</b>: warn band &gt;5%, fail band &gt;20%.</description></item>
///   <item><description><b>Schema match</b>: every field referenced by the manifest's identity remaps must exist on the target.</description></item>
///   <item><description><b>Identity coverage</b>: ratio of source ids that have an identity-remap entry.</description></item>
///   <item><description><b>Attachment coverage</b>: ratio of slice-3 attachment records classified <c>automated</c> that have a target reference.</description></item>
///   <item><description><b>Relationship coverage</b>: ratio of slice-3 relationship records classified <c>automated</c> or <c>assisted</c> that have a target reference.</description></item>
/// </list>
/// </remarks>
public static class ArcGisMigrationParityRunner
{
    private const string ArcGisSourceKind = "arcgis-geoservices-rest";

    /// <summary>
    /// Warn-band threshold for the feature-count probe (absolute delta ratio).
    /// </summary>
    public const double FeatureCountWarnBand = 0.05;

    /// <summary>
    /// Fail-band threshold for the feature-count probe (absolute delta ratio).
    /// </summary>
    public const double FeatureCountFailBand = 0.20;

    /// <summary>
    /// Pass-band threshold for coverage probes (identity, attachment, relationship).
    /// </summary>
    public const double CoveragePassBand = 0.99;

    /// <summary>
    /// Warn-band threshold for coverage probes (identity, attachment, relationship).
    /// </summary>
    public const double CoverageWarnBand = 0.80;

    /// <summary>
    /// Run the parity probes and emit a deterministic artifact.
    /// </summary>
    /// <param name="manifest">Manifest emitted by slices 2-4 for the ArcGIS source.</param>
    /// <param name="inventory">Original ArcGIS inventory the manifest was translated from.</param>
    /// <param name="targetReader">Probe-focused reader over the Honua-applied target.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deterministic parity artifact.</returns>
    /// <exception cref="ArgumentNullException">A required argument was <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The manifest or inventory is not an ArcGIS source.</exception>
    public static async Task<ArcGisMigrationParityArtifact> RunAsync(
        MigrationManifestArtifact manifest,
        MigrationSourceInventoryArtifact inventory,
        IArcGisParityFeatureReader targetReader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(targetReader);

        if (!string.Equals(manifest.SourceKind, ArcGisSourceKind, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Parity runner only supports source kind '{ArcGisSourceKind}'; manifest reports '{manifest.SourceKind}'.",
                nameof(manifest));
        }

        if (!string.Equals(inventory.SourceKind, ArcGisSourceKind, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                $"Parity runner only supports source kind '{ArcGisSourceKind}'; inventory reports '{inventory.SourceKind}'.",
                nameof(inventory));
        }

        var inventoryById = inventory.Resources
            .GroupBy(static r => r.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var probes = new List<ArcGisMigrationParityResourceProbe>(manifest.TargetResources.Length);
        foreach (var target in manifest.TargetResources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            inventoryById.TryGetValue(target.SourceResourceId, out var sourceResource);
            probes.Add(await RunResourceProbesAsync(manifest, target, sourceResource, targetReader, cancellationToken).ConfigureAwait(false));
        }

        var orderedProbes = probes
            .OrderBy(static p => p.TargetResourceId, StringComparer.Ordinal)
            .ThenBy(static p => p.SourceResourceId, StringComparer.Ordinal)
            .ToArray();

        var classification = AggregateClassification(orderedProbes.Select(p => p.Classification));
        var reasons = CollectReasons(orderedProbes);

        return new ArcGisMigrationParityArtifact
        {
            SourceKind = manifest.SourceKind,
            Source = manifest.Source,
            Classification = classification,
            Reasons = reasons,
            ResourceProbes = orderedProbes
        };
    }

    private static async Task<ArcGisMigrationParityResourceProbe> RunResourceProbesAsync(
        MigrationManifestArtifact manifest,
        MigrationManifestTargetResource target,
        MigrationInventoryResource? sourceResource,
        IArcGisParityFeatureReader targetReader,
        CancellationToken cancellationToken)
    {
        var targetCount = await targetReader.GetFeatureCountAsync(target.TargetResourceId, cancellationToken).ConfigureAwait(false);
        var targetFields = await targetReader.GetFieldNamesAsync(target.TargetResourceId, cancellationToken).ConfigureAwait(false);

        var featureCount = ProbeFeatureCount(target.TargetResourceId, sourceResource?.FeatureCount, targetCount);
        var schema = ProbeSchema(manifest, target, targetFields);
        var identity = ProbeIdentityCoverage(manifest, target);
        var attachment = ProbeAttachmentCoverage(target);
        var relationship = ProbeRelationshipCoverage(target);

        var classification = AggregateClassification(
        [
            featureCount.Classification,
            schema.Classification,
            identity.Classification,
            attachment.Classification,
            relationship.Classification
        ]);

        return new ArcGisMigrationParityResourceProbe
        {
            SourceResourceId = target.SourceResourceId,
            TargetResourceId = target.TargetResourceId,
            Classification = classification,
            FeatureCount = featureCount,
            Schema = schema,
            IdentityCoverage = identity,
            AttachmentCoverage = attachment,
            RelationshipCoverage = relationship
        };
    }

    private static ArcGisMigrationParityFeatureCountProbe ProbeFeatureCount(
        string targetResourceId,
        int? sourceCount,
        long? targetCount)
    {
        if (targetCount is null)
        {
            return new ArcGisMigrationParityFeatureCountProbe
            {
                SourceCount = sourceCount,
                TargetCount = 0,
                Delta = null,
                DeltaRatio = null,
                Classification = ArcGisMigrationParityClassifications.Fail,
                Reason = $"Target resource '{targetResourceId}' was not found on the target; feature parity cannot be confirmed."
            };
        }

        if (sourceCount is null)
        {
            return new ArcGisMigrationParityFeatureCountProbe
            {
                SourceCount = null,
                TargetCount = targetCount.Value,
                Delta = null,
                DeltaRatio = null,
                Classification = ArcGisMigrationParityClassifications.Warn,
                Reason = "Source did not advertise a feature count; cannot prove count parity without a baseline."
            };
        }

        var source = (long)sourceCount.Value;
        var delta = targetCount.Value - source;

        if (source == 0)
        {
            return delta == 0
                ? new ArcGisMigrationParityFeatureCountProbe
                {
                    SourceCount = source,
                    TargetCount = targetCount.Value,
                    Delta = 0,
                    DeltaRatio = 0d,
                    Classification = ArcGisMigrationParityClassifications.Pass
                }
                : new ArcGisMigrationParityFeatureCountProbe
                {
                    SourceCount = source,
                    TargetCount = targetCount.Value,
                    Delta = delta,
                    DeltaRatio = null,
                    Classification = ArcGisMigrationParityClassifications.Fail,
                    Reason = $"Source advertised 0 features but target reports {targetCount.Value}; reject the apply."
                };
        }

        var ratio = (double)Math.Abs(delta) / source;
        var classification = ratio <= FeatureCountWarnBand
            ? ArcGisMigrationParityClassifications.Pass
            : ratio <= FeatureCountFailBand
                ? ArcGisMigrationParityClassifications.Warn
                : ArcGisMigrationParityClassifications.Fail;

        string? reason = classification switch
        {
            ArcGisMigrationParityClassifications.Pass => null,
            ArcGisMigrationParityClassifications.Warn =>
                $"Feature-count delta {delta} ({Percent(ratio)}) is outside the {Percent(FeatureCountWarnBand)} pass band; investigate before cutover.",
            _ =>
                $"Feature-count delta {delta} ({Percent(ratio)}) exceeds the {Percent(FeatureCountFailBand)} fail band; reject the apply."
        };

        return new ArcGisMigrationParityFeatureCountProbe
        {
            SourceCount = source,
            TargetCount = targetCount.Value,
            Delta = delta,
            DeltaRatio = ratio,
            Classification = classification,
            Reason = reason
        };
    }

    private static ArcGisMigrationParitySchemaProbe ProbeSchema(
        MigrationManifestArtifact manifest,
        MigrationManifestTargetResource target,
        IReadOnlyList<string>? targetFields)
    {
        var expected = manifest.IdentityRemaps
            .Where(remap => string.Equals(remap.SourceKind, "field", StringComparison.OrdinalIgnoreCase))
            .Where(remap => string.Equals(GetFieldOwner(remap.SourceId), target.SourceResourceId, StringComparison.Ordinal))
            .Select(remap => remap.TargetName)
            .Concat(target.Fields.Select(f => f.Name))
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        if (targetFields is null)
        {
            return new ArcGisMigrationParitySchemaProbe
            {
                ExpectedFieldNames = expected,
                TargetFieldNames = [],
                MissingFieldNames = expected,
                Classification = expected.Length == 0
                    ? ArcGisMigrationParityClassifications.Pass
                    : ArcGisMigrationParityClassifications.Fail,
                Reason = expected.Length == 0
                    ? null
                    : $"Target resource '{target.TargetResourceId}' was not found on the target; schema parity cannot be confirmed."
            };
        }

        var actual = targetFields
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToArray();

        var actualSet = new HashSet<string>(actual, StringComparer.Ordinal);
        var missing = expected
            .Where(name => !actualSet.Contains(name))
            .ToArray();

        return new ArcGisMigrationParitySchemaProbe
        {
            ExpectedFieldNames = expected,
            TargetFieldNames = actual,
            MissingFieldNames = missing,
            Classification = missing.Length == 0
                ? ArcGisMigrationParityClassifications.Pass
                : ArcGisMigrationParityClassifications.Fail,
            Reason = missing.Length == 0
                ? null
                : $"Target is missing {missing.Length} expected field(s): {string.Join(", ", missing)}."
        };
    }

    private static ArcGisMigrationParityCoverageProbe ProbeIdentityCoverage(
        MigrationManifestArtifact manifest,
        MigrationManifestTargetResource target)
    {
        // Expected source ids = resource itself + every related attachment + every related relationship.
        var expectedSourceIds = new HashSet<string>(StringComparer.Ordinal)
        {
            target.SourceResourceId
        };
        foreach (var attachment in target.Attachments)
        {
            expectedSourceIds.Add(attachment.SourceAttachmentId);
        }
        foreach (var relationship in target.Relationships)
        {
            expectedSourceIds.Add(relationship.SourceRelationshipId);
        }

        var remappedSourceIds = manifest.IdentityRemaps
            .Select(remap => remap.SourceId)
            .ToHashSet(StringComparer.Ordinal);

        // Augment with attachment/relationship records that have an inline target ref - slice 3 does
        // not always emit those into IdentityRemaps but the inline ref means an identity was bound.
        foreach (var attachment in target.Attachments.Where(a => !string.IsNullOrWhiteSpace(a.TargetAttachmentRef)))
        {
            remappedSourceIds.Add(attachment.SourceAttachmentId);
        }
        foreach (var relationship in target.Relationships.Where(r => !string.IsNullOrWhiteSpace(r.TargetRelationshipRef)))
        {
            remappedSourceIds.Add(relationship.SourceRelationshipId);
        }

        return BuildCoverageProbe(
            expectedSourceIds.OrderBy(static id => id, StringComparer.Ordinal).ToArray(),
            id => remappedSourceIds.Contains(id),
            probeName: "Identity-remap");
    }

    private static ArcGisMigrationParityCoverageProbe ProbeAttachmentCoverage(MigrationManifestTargetResource target)
    {
        var expected = target.Attachments
            .Where(a => string.Equals(a.Classification, MigrationManifestAttachmentClassifications.Automated, StringComparison.Ordinal))
            .OrderBy(a => a.SourceAttachmentId, StringComparer.Ordinal)
            .ToArray();

        return BuildCoverageProbe(
            expected.Select(a => a.SourceAttachmentId).ToArray(),
            id => expected.First(a => a.SourceAttachmentId == id).TargetAttachmentRef is { Length: > 0 },
            probeName: "Attachment");
    }

    private static ArcGisMigrationParityCoverageProbe ProbeRelationshipCoverage(MigrationManifestTargetResource target)
    {
        var expected = target.Relationships
            .Where(r =>
                string.Equals(r.Classification, MigrationManifestRelationshipClassifications.Automated, StringComparison.Ordinal) ||
                string.Equals(r.Classification, MigrationManifestRelationshipClassifications.Assisted, StringComparison.Ordinal))
            .OrderBy(r => r.SourceRelationshipId, StringComparer.Ordinal)
            .ToArray();

        return BuildCoverageProbe(
            expected.Select(r => r.SourceRelationshipId).ToArray(),
            id => expected.First(r => r.SourceRelationshipId == id).TargetRelationshipRef is { Length: > 0 },
            probeName: "Relationship");
    }

    private static ArcGisMigrationParityCoverageProbe BuildCoverageProbe(
        string[] expectedIds,
        Func<string, bool> isCovered,
        string probeName)
    {
        if (expectedIds.Length == 0)
        {
            return new ArcGisMigrationParityCoverageProbe
            {
                Expected = 0,
                Observed = 0,
                Coverage = 1.0,
                MissingIds = [],
                Classification = ArcGisMigrationParityClassifications.Pass,
                Reason = $"{probeName} coverage probe had no expected items; recording as pass."
            };
        }

        var missing = expectedIds.Where(id => !isCovered(id))
            .OrderBy(static id => id, StringComparer.Ordinal)
            .ToArray();
        var observed = expectedIds.Length - missing.Length;
        var coverage = (double)observed / expectedIds.Length;

        var classification = coverage >= CoveragePassBand
            ? ArcGisMigrationParityClassifications.Pass
            : coverage >= CoverageWarnBand
                ? ArcGisMigrationParityClassifications.Warn
                : ArcGisMigrationParityClassifications.Fail;

        string? reason = classification == ArcGisMigrationParityClassifications.Pass
            ? null
            : $"{probeName} coverage {Percent(coverage)} is outside the {Percent(CoveragePassBand)} pass band ({missing.Length} of {expectedIds.Length} expected item(s) missing target reference).";

        return new ArcGisMigrationParityCoverageProbe
        {
            Expected = expectedIds.Length,
            Observed = observed,
            Coverage = coverage,
            MissingIds = missing,
            Classification = classification,
            Reason = reason
        };
    }

    private static string AggregateClassification(IEnumerable<string> classifications)
    {
        var materialized = classifications.Select(NormalizeClassification).ToArray();
        if (materialized.Length == 0)
        {
            return ArcGisMigrationParityClassifications.Pass;
        }

        if (materialized.Any(static c => c == ArcGisMigrationParityClassifications.Fail))
        {
            return ArcGisMigrationParityClassifications.Fail;
        }

        if (materialized.Any(static c => c == ArcGisMigrationParityClassifications.Warn))
        {
            return ArcGisMigrationParityClassifications.Warn;
        }

        return ArcGisMigrationParityClassifications.Pass;
    }

    private static string NormalizeClassification(string value)
        => value switch
        {
            ArcGisMigrationParityClassifications.Pass => ArcGisMigrationParityClassifications.Pass,
            ArcGisMigrationParityClassifications.Warn => ArcGisMigrationParityClassifications.Warn,
            ArcGisMigrationParityClassifications.Fail => ArcGisMigrationParityClassifications.Fail,
            _ => ArcGisMigrationParityClassifications.Warn
        };

    private static string[] CollectReasons(IReadOnlyList<ArcGisMigrationParityResourceProbe> probes)
        => probes
            .SelectMany(static probe => new[]
            {
                probe.FeatureCount.Reason,
                probe.Schema.Reason,
                probe.IdentityCoverage.Reason,
                probe.AttachmentCoverage.Reason,
                probe.RelationshipCoverage.Reason
            })
            .Where(static reason => !string.IsNullOrWhiteSpace(reason))
            .Select(static reason => reason!.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static reason => reason, StringComparer.Ordinal)
            .ToArray();

    private static string GetFieldOwner(string sourceId)
    {
        // Field identities are emitted as "<resourceId>:field:<fieldName>" by upstream slices when
        // they choose to enumerate per-field identity remaps. When the format is different we fall
        // back to returning the whole id so the schema probe still does an exact match against the
        // target's resource id (i.e. it simply skips that remap).
        var marker = sourceId.IndexOf(":field:", StringComparison.Ordinal);
        return marker < 0 ? sourceId : sourceId[..marker];
    }

    private static string Percent(double ratio)
        => (ratio * 100d).ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + "%";
}
