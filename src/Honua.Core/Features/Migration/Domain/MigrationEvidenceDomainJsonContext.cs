// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Honua.Core.Features.Migration.Domain;

/// <summary>
/// Source-generated JSON context for migration evidence domain models.
/// </summary>
[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(MigrationEvidenceRequest))]
[JsonSerializable(typeof(MigrationEvidenceLayerMapping))]
[JsonSerializable(typeof(MigrationEvidenceReport))]
[JsonSerializable(typeof(MigrationEvidenceReportSummary))]
[JsonSerializable(typeof(MigrationEvidenceReport[]))]
[JsonSerializable(typeof(MigrationEvidenceReportSummary[]))]
[JsonSerializable(typeof(MigrationEvidenceSourceBaseline))]
[JsonSerializable(typeof(MigrationEvidenceTargetSnapshot))]
[JsonSerializable(typeof(MigrationEvidenceLayerSnapshot))]
[JsonSerializable(typeof(MigrationEvidenceFieldSnapshot))]
[JsonSerializable(typeof(MigrationEvidenceExtentSnapshot))]
[JsonSerializable(typeof(MigrationEvidenceOperationalSnapshot))]
[JsonSerializable(typeof(MigrationEvidenceComparison))]
[JsonSerializable(typeof(MigrationComparisonCheck))]
[JsonSerializable(typeof(MigrationComparisonObservation))]
[JsonSerializable(typeof(MigrationEvidenceReadinessSummary))]
[JsonSerializable(typeof(CutoverChecklistItem))]
[JsonSerializable(typeof(MigrationEvidenceProgress))]
[JsonSerializable(typeof(MigrationEvidenceProvider))]
[JsonSerializable(typeof(MigrationCutoverProfile))]
[JsonSerializable(typeof(MigrationReadinessState))]
[JsonSerializable(typeof(MigrationEvidenceStatus))]
[JsonSerializable(typeof(MigrationEvidenceJobStatus))]
public sealed partial class MigrationEvidenceDomainJsonContext : JsonSerializerContext
{
}
