// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Core.Features.Metadata.Domain.V2;

public sealed record MetadataV2ConnectionSecrets
{
    [JsonPropertyName("endpointRef")]
    public MetadataV2SecretReference? EndpointRef { get; init; }

    [JsonPropertyName("credentialRef")]
    public MetadataV2SecretReference? CredentialRef { get; init; }

    [JsonPropertyName("connectionRef")]
    public MetadataV2SecretReference? ConnectionRef { get; init; }

    [JsonPropertyName("inlineDevOnly")]
    public MetadataV2InlineDevelopmentSecretMarker? InlineDevOnly { get; init; }

    [JsonIgnore]
    public bool HasExternalReferences => EndpointRef is not null || CredentialRef is not null || ConnectionRef is not null;

    public MetadataV2ConnectionSecrets Redacted()
        => this with
        {
            EndpointRef = EndpointRef?.Redacted(),
            CredentialRef = CredentialRef?.Redacted(),
            ConnectionRef = ConnectionRef?.Redacted()
        };

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

public sealed record MetadataV2SecretReference
{
    [JsonPropertyName("provider")]
    public string Provider { get; init; } = string.Empty;

    [JsonPropertyName("ref")]
    public string Reference { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string? Version { get; init; }

    public static MetadataV2SecretReference Create(string provider, string reference, string? version = null)
        => new()
        {
            Provider = provider,
            Reference = reference,
            Version = version
        };

    public MetadataV2SecretReference Redacted()
        => this with
        {
            Reference = Redact(Reference),
            Version = Version is null ? null : "***"
        };

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

public sealed record MetadataV2InlineDevelopmentSecretMarker
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    [JsonPropertyName("owner")]
    public string? Owner { get; init; }
}

public sealed record MetadataV2SecretReferenceValidationIssue(string Path, string Message);
