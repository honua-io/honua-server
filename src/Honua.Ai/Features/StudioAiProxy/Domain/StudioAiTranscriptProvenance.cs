// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Ai.StudioAiProxy.Domain;

/// <summary>Replay-resistant identity supplied by a release-certifying Studio call.</summary>
public sealed class StudioAiTranscriptCertification
{
    [JsonPropertyName("candidateId")] public string CandidateId { get; init; } = string.Empty;
    [JsonPropertyName("releaseId")] public string ReleaseId { get; init; } = string.Empty;
    [JsonPropertyName("endpointIdentity")] public string EndpointIdentity { get; init; } = string.Empty;
    [JsonPropertyName("actionId")] public string ActionId { get; init; } = string.Empty;
    [JsonPropertyName("runNonce")] public string RunNonce { get; init; } = string.Empty;
}

/// <summary>Detached Ed25519 signature over <see cref="CanonicalTranscript"/>.</summary>
public sealed class StudioAiSignedTranscript
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; init; } = "honua.studio-ai.transcript.v1";
    [JsonPropertyName("canonicalization")] public string Canonicalization { get; init; } = "honua-canonical-json-v1";
    [JsonPropertyName("digestAlgorithm")] public string DigestAlgorithm { get; init; } = "sha-256";
    [JsonPropertyName("signatureAlgorithm")] public string SignatureAlgorithm { get; init; } = "Ed25519";
    [JsonPropertyName("keyId")] public required string KeyId { get; init; }
    [JsonPropertyName("canonicalTranscript")] public required string CanonicalTranscript { get; init; }
    [JsonPropertyName("transcriptDigest")] public required string TranscriptDigest { get; init; }
    [JsonPropertyName("signature")] public required string Signature { get; init; }
}

/// <summary>Public verification material published with the proxy capability evidence.</summary>
public sealed class StudioAiTranscriptSigningManifest
{
    [JsonPropertyName("requiredForCertification")] public bool RequiredForCertification { get; init; } = true;
    [JsonPropertyName("keys")] public IReadOnlyList<StudioAiTranscriptVerificationKey> Keys { get; init; } = [];
}

/// <summary>One active or overlap-window Ed25519 verification key.</summary>
public sealed class StudioAiTranscriptVerificationKey
{
    [JsonPropertyName("keyId")] public required string KeyId { get; init; }
    [JsonPropertyName("algorithm")] public string Algorithm { get; init; } = "Ed25519";
    [JsonPropertyName("publicKey")] public required string PublicKey { get; init; }
    [JsonPropertyName("fingerprint")] public required string Fingerprint { get; init; }
    [JsonPropertyName("notBefore")] public DateTimeOffset? NotBefore { get; init; }
    [JsonPropertyName("notAfter")] public DateTimeOffset? NotAfter { get; init; }
    [JsonPropertyName("revoked")] public bool Revoked { get; init; }
}
