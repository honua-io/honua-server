// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Forms.Packages;

/// <summary>
/// Stable compatibility classification for an offline form package version
/// relative to the current published version. Offline field clients use this to
/// decide whether pending offline edits can be submitted as-is, require a
/// non-breaking refresh, or must be migrated before submitting.
/// </summary>
public static class FormCompatibilityLevel
{
    /// <summary>
    /// The offline version matches the current published version. No client
    /// action is required.
    /// </summary>
    public const string Current = "current";

    /// <summary>
    /// A newer published version exists but its submission-affecting policy
    /// (target, submit policy, attachment policy) is unchanged, so existing
    /// offline edits remain submittable. Clients should refresh when convenient.
    /// </summary>
    public const string Compatible = "compatible";

    /// <summary>
    /// A newer published version changes submission-affecting policy. Offline
    /// edits captured against the older version may be rejected and should be
    /// migrated/re-validated before submitting.
    /// </summary>
    public const string Breaking = "breaking";

    /// <summary>
    /// The requested version is unknown, archived, or no published version
    /// exists. Clients must re-provision the form before collecting.
    /// </summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// Offline compatibility and migration manifest for a published form package.
/// Surfaces the version, compatibility level, and migration signals an offline
/// mobile client needs to reconcile pending edits with the server-current
/// published version without defining mobile-local DTOs.
/// </summary>
public sealed class FormCompatibilityManifest
{
    /// <summary>Stable package id.</summary>
    [JsonPropertyName("formId")]
    public string FormId { get; init; } = string.Empty;

    /// <summary>The version the client is asking about (its locally-cached version).</summary>
    [JsonPropertyName("clientVersion")]
    public int? ClientVersion { get; init; }

    /// <summary>The current server-published version, when one exists.</summary>
    [JsonPropertyName("currentPublishedVersion")]
    public int? CurrentPublishedVersion { get; init; }

    /// <summary>
    /// Compatibility classification of the client version against the current
    /// published version. One of <see cref="FormCompatibilityLevel"/>.
    /// </summary>
    [JsonPropertyName("compatibility")]
    public string Compatibility { get; init; } = FormCompatibilityLevel.Unknown;

    /// <summary>
    /// Whether offline edits captured against the client version may be
    /// submitted against the current published version without migration.
    /// </summary>
    [JsonPropertyName("offlineEditsSubmittable")]
    public bool OfflineEditsSubmittable { get; init; }

    /// <summary>
    /// Whether the client should refresh its cached form package to the current
    /// published version.
    /// </summary>
    [JsonPropertyName("refreshRecommended")]
    public bool RefreshRecommended { get; init; }

    /// <summary>
    /// Whether the client must migrate or re-validate pending edits before
    /// submitting them against the current published version.
    /// </summary>
    [JsonPropertyName("migrationRequired")]
    public bool MigrationRequired { get; init; }

    /// <summary>Content hash of the client version, when known.</summary>
    [JsonPropertyName("clientContentHash")]
    public string? ClientContentHash { get; init; }

    /// <summary>Content hash of the current published version, when one exists.</summary>
    [JsonPropertyName("currentContentHash")]
    public string? CurrentContentHash { get; init; }

    /// <summary>Policy hash of the client version, when known.</summary>
    [JsonPropertyName("clientPolicyHash")]
    public string? ClientPolicyHash { get; init; }

    /// <summary>Policy hash of the current published version, when one exists.</summary>
    [JsonPropertyName("currentPolicyHash")]
    public string? CurrentPolicyHash { get; init; }

    /// <summary>
    /// Ordered migration signals describing what changed between the client
    /// version and the current published version. Empty when compatible.
    /// </summary>
    [JsonPropertyName("migrationSignals")]
    public FormMigrationSignal[] MigrationSignals { get; init; } = [];
}

/// <summary>
/// A single, machine-readable migration signal describing a change between two
/// published form package versions that affects offline clients.
/// </summary>
public sealed class FormMigrationSignal
{
    /// <summary>
    /// Stable change code, e.g. <c>targetChanged</c>, <c>policyChanged</c>,
    /// <c>contentChanged</c>.
    /// </summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    /// <summary>
    /// Severity of the change: <c>breaking</c> when offline edits may be
    /// rejected, otherwise <c>info</c>.
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "info";

    /// <summary>Human-readable, policy-safe description of the change.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
