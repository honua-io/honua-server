// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Forms.Packages;

public static class FormCompatibilityLevel
{
    public const string Current = "current";

    public const string Compatible = "compatible";

    public const string Breaking = "breaking";

    // An unknown or unpublished version means the client must re-provision the form before collecting.
    public const string Unknown = "unknown";
}

public sealed class FormCompatibilityManifest
{
    [JsonPropertyName("formId")]
    public string FormId { get; init; } = string.Empty;

    [JsonPropertyName("clientVersion")]
    public int? ClientVersion { get; init; }

    [JsonPropertyName("currentPublishedVersion")]
    public int? CurrentPublishedVersion { get; init; }

    [JsonPropertyName("compatibility")]
    public string Compatibility { get; init; } = FormCompatibilityLevel.Unknown;

    [JsonPropertyName("offlineEditsSubmittable")]
    public bool OfflineEditsSubmittable { get; init; }

    [JsonPropertyName("refreshRecommended")]
    public bool RefreshRecommended { get; init; }

    [JsonPropertyName("migrationRequired")]
    public bool MigrationRequired { get; init; }

    [JsonPropertyName("clientContentHash")]
    public string? ClientContentHash { get; init; }

    [JsonPropertyName("currentContentHash")]
    public string? CurrentContentHash { get; init; }

    [JsonPropertyName("clientPolicyHash")]
    public string? ClientPolicyHash { get; init; }

    [JsonPropertyName("currentPolicyHash")]
    public string? CurrentPolicyHash { get; init; }

    [JsonPropertyName("migrationSignals")]
    public FormMigrationSignal[] MigrationSignals { get; init; } = [];
}

public sealed class FormMigrationSignal
{
    [JsonPropertyName("code")]
    public string Code { get; init; } = string.Empty;

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "info";

    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;
}
