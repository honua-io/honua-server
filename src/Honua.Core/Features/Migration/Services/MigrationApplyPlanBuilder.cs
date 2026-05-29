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
/// Builds deterministic apply-plan artifacts from migration manifests.
/// </summary>
public static class MigrationApplyPlanBuilder
{
    private const string ReadyDisposition = "ready";
    private const string ManualReviewDisposition = "manual-review";
    private const string UnsupportedDisposition = "unsupported";

    /// <summary>
    /// Build an ordered, replayable apply plan from a migration manifest.
    /// </summary>
    /// <param name="manifest">Source migration manifest.</param>
    /// <returns>Deterministic apply plan artifact.</returns>
    public static MigrationApplyPlanArtifact Build(MigrationManifestArtifact manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);

        var manualReviewItems = OrderReviewItems(manifest.ManualReviewItems);
        var unsupportedItems = OrderReviewItems(manifest.UnsupportedItems);
        var reviewCodesBySource = BuildReviewCodesBySource(manualReviewItems, unsupportedItems);
        var unsupportedSourceIds = unsupportedItems
            .Select(static item => item.SourceId)
            .ToHashSet(StringComparer.Ordinal);

        var steps = new List<MigrationApplyPlanStep>();

        foreach (var resource in manifest.TargetResources.OrderBy(static item => item.SourceResourceId, StringComparer.Ordinal))
        {
            var disposition = string.Equals(resource.Action, "publish", StringComparison.Ordinal)
                ? ReadyDisposition
                : ManualReviewDisposition;

            steps.Add(new MigrationApplyPlanStep
            {
                Sequence = steps.Count + 1,
                StepId = $"resource:{resource.SourceResourceId}",
                SourceId = resource.SourceResourceId,
                Kind = resource.SourceKind,
                Action = disposition == ReadyDisposition ? "stage-catalog-resource" : ManualReviewDisposition,
                Disposition = disposition,
                TargetServiceName = resource.TargetServiceName,
                TargetResourceName = resource.TargetResourceName,
                StyleIds = Order(resource.StyleIds),
                ExternalDependencyIds = Order(resource.ExternalDependencyIds),
                ReviewCodes = GetReviewCodes(reviewCodesBySource, resource.SourceResourceId),
                Compatibility = resource.Compatibility
            });
        }

        foreach (var style in manifest.StyleActions.OrderBy(static item => item.SourceStyleId, StringComparer.Ordinal))
        {
            var disposition = unsupportedSourceIds.Contains(style.SourceStyleId)
                ? UnsupportedDisposition
                : string.Equals(style.Action, "import", StringComparison.Ordinal)
                    ? ReadyDisposition
                    : ManualReviewDisposition;

            steps.Add(new MigrationApplyPlanStep
            {
                Sequence = steps.Count + 1,
                StepId = $"style:{style.SourceStyleId}",
                SourceId = style.SourceStyleId,
                Kind = "style",
                Action = disposition switch
                {
                    ReadyDisposition => "stage-style",
                    UnsupportedDisposition => UnsupportedDisposition,
                    _ => ManualReviewDisposition
                },
                Disposition = disposition,
                ExternalDependencyIds = Order(style.ExternalDependencyIds),
                ReviewCodes = GetReviewCodes(reviewCodesBySource, style.SourceStyleId),
                Compatibility = style.Compatibility
            });
        }

        var orderedSteps = steps
            .OrderBy(static step => step.Sequence)
            .ToArray();
        var summary = new MigrationApplyPlanSummary
        {
            TotalStepCount = orderedSteps.Length,
            ReadyStepCount = orderedSteps.Count(static step => step.Disposition == ReadyDisposition),
            ManualReviewStepCount = orderedSteps.Count(static step => step.Disposition == ManualReviewDisposition),
            UnsupportedStepCount = orderedSteps.Count(static step => step.Disposition == UnsupportedDisposition),
            UnsupportedItemCount = unsupportedItems.Length
        };
        var fingerprint = ComputeFingerprint(
            manifest.SourceKind,
            manifest.Source,
            summary,
            orderedSteps,
            manualReviewItems,
            unsupportedItems);

        return new MigrationApplyPlanArtifact
        {
            SourceManifestArtifactKind = manifest.ArtifactKind,
            SourceManifestArtifactVersion = manifest.ArtifactVersion,
            SourceKind = manifest.SourceKind,
            Source = manifest.Source,
            ReplayToken = fingerprint,
            PlanFingerprint = fingerprint,
            Summary = summary,
            Steps = orderedSteps,
            ManualReviewItems = manualReviewItems,
            UnsupportedItems = unsupportedItems
        };
    }

    private static MigrationManifestReviewItem[] OrderReviewItems(IEnumerable<MigrationManifestReviewItem> items)
        => items
            .OrderBy(static item => item.SourceId, StringComparer.Ordinal)
            .ThenBy(static item => item.Code, StringComparer.Ordinal)
            .ToArray();

    private static Dictionary<string, string[]> BuildReviewCodesBySource(
        IReadOnlyList<MigrationManifestReviewItem> manualReviewItems,
        IReadOnlyList<MigrationManifestReviewItem> unsupportedItems)
        => manualReviewItems
            .Concat(unsupportedItems)
            .GroupBy(static item => item.SourceId, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => Order(group.Select(static item => item.Code)),
                StringComparer.Ordinal);

    private static string[] GetReviewCodes(Dictionary<string, string[]> reviewCodesBySource, string sourceId)
        => reviewCodesBySource.TryGetValue(sourceId, out var codes) ? codes : [];

    private static string[] Order(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string ComputeFingerprint(
        string sourceKind,
        MigrationSourceIdentity source,
        MigrationApplyPlanSummary summary,
        IReadOnlyList<MigrationApplyPlanStep> steps,
        IReadOnlyList<MigrationManifestReviewItem> manualReviewItems,
        IReadOnlyList<MigrationManifestReviewItem> unsupportedItems)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("sourceKind", sourceKind);
            WriteSource(writer, source);
            WriteSummary(writer, summary);
            WriteSteps(writer, steps);
            WriteReviewItems(writer, "manualReviewItems", manualReviewItems);
            WriteReviewItems(writer, "unsupportedItems", unsupportedItems);
            writer.WriteEndObject();
        }

        var hash = SHA256.HashData(stream.ToArray());
        return $"sha256:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void WriteSource(Utf8JsonWriter writer, MigrationSourceIdentity source)
    {
        writer.WriteStartObject("source");
        writer.WriteString("displayName", source.DisplayName);
        writer.WriteString("baseUrl", source.BaseUrl);
        WriteNullableString(writer, "product", source.Product);
        WriteNullableString(writer, "version", source.Version);
        WriteNullableString(writer, "build", source.Build);
        WriteNullableString(writer, "serviceType", source.ServiceType);
        writer.WriteEndObject();
    }

    private static void WriteSummary(Utf8JsonWriter writer, MigrationApplyPlanSummary summary)
    {
        writer.WriteStartObject("summary");
        writer.WriteNumber("totalStepCount", summary.TotalStepCount);
        writer.WriteNumber("readyStepCount", summary.ReadyStepCount);
        writer.WriteNumber("manualReviewStepCount", summary.ManualReviewStepCount);
        writer.WriteNumber("unsupportedStepCount", summary.UnsupportedStepCount);
        writer.WriteNumber("unsupportedItemCount", summary.UnsupportedItemCount);
        writer.WriteEndObject();
    }

    private static void WriteSteps(Utf8JsonWriter writer, IEnumerable<MigrationApplyPlanStep> steps)
    {
        writer.WriteStartArray("steps");
        foreach (var step in steps)
        {
            writer.WriteStartObject();
            writer.WriteNumber("sequence", step.Sequence);
            writer.WriteString("stepId", step.StepId);
            writer.WriteString("sourceId", step.SourceId);
            writer.WriteString("kind", step.Kind);
            writer.WriteString("action", step.Action);
            writer.WriteString("disposition", step.Disposition);
            WriteNullableString(writer, "targetServiceName", step.TargetServiceName);
            WriteNullableString(writer, "targetResourceName", step.TargetResourceName);
            WriteStringArray(writer, "styleIds", step.StyleIds);
            WriteStringArray(writer, "externalDependencyIds", step.ExternalDependencyIds);
            WriteStringArray(writer, "reviewCodes", step.ReviewCodes);
            WriteCompatibility(writer, step.Compatibility);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteReviewItems(Utf8JsonWriter writer, string propertyName, IEnumerable<MigrationManifestReviewItem> items)
    {
        writer.WriteStartArray(propertyName);
        foreach (var item in items)
        {
            writer.WriteStartObject();
            writer.WriteString("sourceId", item.SourceId);
            writer.WriteString("kind", item.Kind);
            writer.WriteString("code", item.Code);
            writer.WriteString("severity", item.Severity);
            writer.WriteString("reason", item.Reason);
            WriteStringArray(writer, "manualSteps", item.ManualSteps);
            WriteStringArray(writer, "warnings", item.Warnings);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteCompatibility(Utf8JsonWriter writer, MigrationCompatibilityAssessment compatibility)
    {
        writer.WriteStartObject("compatibility");
        writer.WriteString("level", compatibility.Level);
        WriteNullableString(writer, "code", compatibility.Code);
        writer.WriteString("reason", compatibility.Reason);
        WriteStringArray(writer, "warnings", compatibility.Warnings);
        WriteStringArray(writer, "manualSteps", compatibility.ManualSteps);
        writer.WriteEndObject();
    }

    private static void WriteStringArray(Utf8JsonWriter writer, string propertyName, IEnumerable<string> values)
    {
        writer.WriteStartArray(propertyName);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value == null)
        {
            writer.WriteNull(propertyName);
            return;
        }

        writer.WriteString(propertyName, value);
    }
}
