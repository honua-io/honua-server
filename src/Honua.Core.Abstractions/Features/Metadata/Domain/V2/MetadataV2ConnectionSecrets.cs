// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

/// <summary>
/// Connection secret references used by metadata v2 connection resources.
/// </summary>
public sealed record MetadataV2ConnectionSecrets
{
    /// <summary>
    /// Reference to endpoint material such as host, port, and database name.
    /// </summary>
    [JsonPropertyName("endpointRef")]
    public MetadataV2SecretReference? EndpointRef { get; init; }

    /// <summary>
    /// Reference to credential material such as username and password.
    /// </summary>
    [JsonPropertyName("credentialRef")]
    public MetadataV2SecretReference? CredentialRef { get; init; }

    /// <summary>
    /// Reference to a complete connection secret.
    /// </summary>
    [JsonPropertyName("connectionRef")]
    public MetadataV2SecretReference? ConnectionRef { get; init; }

    /// <summary>
    /// Explicit marker for inline development-only connection material.
    /// </summary>
    [JsonPropertyName("inlineDevOnly")]
    public MetadataV2InlineDevelopmentSecretMarker? InlineDevOnly { get; init; }

    /// <summary>
    /// Indicates whether any external reference is present.
    /// </summary>
    [JsonIgnore]
    public bool HasExternalReferences => EndpointRef is not null || CredentialRef is not null || ConnectionRef is not null;

    /// <summary>
    /// Returns a copy that is safe to expose in diagnostics and logs.
    /// </summary>
    public MetadataV2ConnectionSecrets Redacted()
        => this with
        {
            EndpointRef = EndpointRef?.Redacted(),
            CredentialRef = CredentialRef?.Redacted(),
            ConnectionRef = ConnectionRef?.Redacted()
        };

    /// <summary>
    /// Validates the shape of the reference set without resolving secrets.
    /// </summary>
    public IReadOnlyList<MetadataV2SecretReferenceValidationIssue> Validate()
    {
        var issues = new List<MetadataV2SecretReferenceValidationIssue>();

        ValidateReference(issues, "endpointRef", EndpointRef);
        ValidateReference(issues, "credentialRef", CredentialRef);
        ValidateReference(issues, "connectionRef", ConnectionRef);

        if (ConnectionRef is not null && (EndpointRef is not null || CredentialRef is not null))
        {
            issues.Add(new MetadataV2SecretReferenceValidationIssue(
                "connectionRef",
                "connectionRef is mutually exclusive with endpointRef and credentialRef."));
        }

        if (InlineDevOnly?.Enabled == true && HasExternalReferences)
        {
            issues.Add(new MetadataV2SecretReferenceValidationIssue(
                "inlineDevOnly",
                "inlineDevOnly cannot be combined with external secret references."));
        }

        if (InlineDevOnly?.Enabled == true && string.IsNullOrWhiteSpace(InlineDevOnly.Reason))
        {
            issues.Add(new MetadataV2SecretReferenceValidationIssue(
                "inlineDevOnly.reason",
                "inlineDevOnly requires a reason."));
        }

        return issues;
    }

    private static void ValidateReference(
        List<MetadataV2SecretReferenceValidationIssue> issues,
        string path,
        MetadataV2SecretReference? reference)
    {
        if (reference is null)
        {
            return;
        }

        foreach (var issue in reference.Validate(path))
        {
            issues.Add(issue);
        }
    }
}

/// <summary>
/// Reference to secret material managed outside the metadata document.
/// </summary>
public sealed record MetadataV2SecretReference
{
    /// <summary>
    /// Secret provider or registry name, for example <c>env</c>, <c>azure-key-vault</c>, or <c>connection-registry</c>.
    /// </summary>
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    /// <summary>
    /// Provider-local reference name or path.
    /// </summary>
    [JsonPropertyName("ref")]
    public string Reference { get; init; } = string.Empty;

    /// <summary>
    /// Optional provider-local version, stage, or label.
    /// </summary>
    [JsonPropertyName("version")]
    public string? Version { get; init; }

    /// <summary>
    /// Creates a secret reference value.
    /// </summary>
    public static MetadataV2SecretReference Create(string provider, string reference, string? version = null)
        => new()
        {
            Provider = provider,
            Reference = reference,
            Version = version
        };

    /// <summary>
    /// Returns a copy that preserves provider shape while hiding provider-local identifiers.
    /// </summary>
    public MetadataV2SecretReference Redacted()
        => this with
        {
            Reference = Redact(Reference),
            Version = Version is null ? null : "***"
        };

    /// <summary>
    /// Validates the reference shape without resolving it.
    /// </summary>
    public IReadOnlyList<MetadataV2SecretReferenceValidationIssue> Validate(string path)
    {
        var issues = new List<MetadataV2SecretReferenceValidationIssue>();

        if (string.IsNullOrWhiteSpace(Provider))
        {
            issues.Add(new MetadataV2SecretReferenceValidationIssue($"{path}.provider", "provider is required."));
        }

        if (string.IsNullOrWhiteSpace(Reference))
        {
            issues.Add(new MetadataV2SecretReferenceValidationIssue($"{path}.ref", "ref is required."));
        }

        if (Provider.Contains(':', StringComparison.Ordinal) || Provider.Contains('/', StringComparison.Ordinal))
        {
            issues.Add(new MetadataV2SecretReferenceValidationIssue(
                $"{path}.provider",
                "provider must be a provider name, not a full secret URI."));
        }

        return issues;
    }

    /// <inheritdoc />
    public override string ToString() => $"{Provider}:{Redact(Reference)}";

    private static string Redact(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "***";
        }

        var trimmed = value.Trim();
        var lastSeparator = trimmed.LastIndexOfAny(['/', ':']);
        var leaf = lastSeparator >= 0 ? trimmed[(lastSeparator + 1)..] : trimmed;
        if (leaf.Length <= 4)
        {
            return "***";
        }

        return string.Concat(leaf.AsSpan(0, 2), "***", leaf.AsSpan(leaf.Length - 2, 2));
    }
}

/// <summary>
/// Explicit marker for inline connection material that must remain development-only.
/// </summary>
public sealed record MetadataV2InlineDevelopmentSecretMarker
{
    /// <summary>
    /// Indicates whether inline development-only material is present.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    /// <summary>
    /// Human-readable explanation for allowing inline material in a development fixture.
    /// </summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>
    /// Optional owner responsible for removing or externalizing the inline material.
    /// </summary>
    [JsonPropertyName("owner")]
    public string? Owner { get; init; }
}

/// <summary>
/// Shape validation issue for metadata v2 secret references.
/// </summary>
public sealed record MetadataV2SecretReferenceValidationIssue(string Path, string Message);
