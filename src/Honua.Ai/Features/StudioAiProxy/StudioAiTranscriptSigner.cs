// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.Security.Abstractions;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Honua.Ai.StudioAiProxy;

internal sealed class StudioAiTranscriptSigner(
    IOptions<StudioAiProxyConfiguration> options,
    TimeProvider timeProvider,
    ISecretProvider? secretProvider = null)
{
    internal const string UnavailableCode = "studio_ai/provenance_signing_unavailable";
    private readonly StudioAiTranscriptSigningOptions _options = options.Value.TranscriptSigning;

    public async Task<SigningKey?> ResolveKeyAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.KeyId)
            || string.IsNullOrWhiteSpace(_options.PrivateKeyReference)
            || secretProvider is null
            || !secretProvider.IsSecretReference(_options.PrivateKeyReference))
        {
            return null;
        }

        string? encoded;
        try
        {
            encoded = await secretProvider.GetSecretOrDefaultAsync(
                _options.PrivateKeyReference,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Resolution failures are deliberately collapsed into the typed unavailable result.
            // Do not log the exception: provider errors can contain the reference or secret details.
            return null;
        }
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        byte[] seed;
        try
        {
            seed = Convert.FromBase64String(encoded);
        }
        catch (FormatException)
        {
            return null;
        }

        if (seed.Length != Ed25519PrivateKeyParameters.KeySize)
        {
            CryptographicOperations.ZeroMemory(seed);
            return null;
        }

        try
        {
            var privateKey = new Ed25519PrivateKeyParameters(seed, 0);
            return new SigningKey(_options.KeyId, privateKey, privateKey.GeneratePublicKey().GetEncoded());
        }
        finally
        {
            CryptographicOperations.ZeroMemory(seed);
        }
    }

    public StudioAiSignedTranscript Sign(
        SigningKey key,
        StudioAiChatRequest request,
        string provider,
        string model,
        IReadOnlyList<StudioAiChatEvent> events)
    {
        var issuedAt = timeProvider.GetUtcNow();
        var requestBytes = Canonicalize(JsonSerializer.SerializeToUtf8Bytes(
            request, StudioAiProxyJsonContext.Default.StudioAiChatRequest));
        var eventBytes = Canonicalize(JsonSerializer.SerializeToUtf8Bytes(
            events.ToList(), StudioAiProxyJsonContext.Default.ListStudioAiChatEvent));
        var selectedResponse = string.Concat(events.Where(e => e.Type == StudioAiChatEventType.TextDelta).Select(e => e.Text));
        var terminalDigest = SHA256.HashData(eventBytes);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            var binding = request.Certification!;
            writer.WriteStartObject();
            writer.WriteString("actionId", binding.ActionId);
            writer.WriteString("candidateId", binding.CandidateId);
            writer.WriteString("canonicalization", "honua-canonical-json-v1");
            writer.WriteString("digestAlgorithm", "sha-256");
            writer.WriteString("endpointIdentity", binding.EndpointIdentity);
            writer.WriteString("expiresAt", issuedAt.AddSeconds(_options.LifetimeSeconds));
            writer.WriteString("issuedAt", issuedAt);
            writer.WriteString("keyId", key.KeyId);
            writer.WriteString("model", model);
            writer.WriteString("provider", provider);
            writer.WriteBase64String("providerEvents", eventBytes);
            writer.WriteString("releaseId", binding.ReleaseId);
            writer.WriteBase64String("request", requestBytes);
            writer.WriteString("runNonce", binding.RunNonce);
            writer.WriteString("schemaVersion", "honua.studio-ai.transcript.v1");
            writer.WriteString("selectedResponse", selectedResponse);
            writer.WriteBase64String("terminalResultDigest", terminalDigest);
            writer.WriteEndObject();
        }

        var canonicalTranscript = buffer.WrittenSpan.ToArray();
        var signer = new Ed25519Signer();
        signer.Init(true, key.PrivateKey);
        signer.BlockUpdate(canonicalTranscript, 0, canonicalTranscript.Length);
        var signature = signer.GenerateSignature();
        return new StudioAiSignedTranscript
        {
            KeyId = key.KeyId,
            CanonicalTranscript = Convert.ToBase64String(canonicalTranscript),
            TranscriptDigest = Convert.ToHexStringLower(SHA256.HashData(canonicalTranscript)),
            Signature = Convert.ToBase64String(signature)
        };
    }

    public async Task<StudioAiTranscriptSigningManifest> GetManifestAsync(CancellationToken cancellationToken)
    {
        var keys = new List<StudioAiTranscriptVerificationKey>();
        var active = await ResolveKeyAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null)
        {
            keys.Add(ToManifestKey(active.KeyId, active.PublicKey, null, null, false));
        }

        foreach (var overlap in _options.OverlapKeys)
        {
            byte[] publicKey;
            try { publicKey = Convert.FromBase64String(overlap.PublicKey); }
            catch (FormatException) { continue; }
            if (publicKey.Length == Ed25519PublicKeyParameters.KeySize)
            {
                keys.Add(ToManifestKey(overlap.KeyId, publicKey, overlap.NotBefore, overlap.NotAfter, overlap.Revoked));
            }
        }

        return new StudioAiTranscriptSigningManifest { Keys = keys };
    }

    private static StudioAiTranscriptVerificationKey ToManifestKey(
        string keyId, byte[] publicKey, DateTimeOffset? notBefore, DateTimeOffset? notAfter, bool revoked)
        => new()
        {
            KeyId = keyId,
            PublicKey = Convert.ToBase64String(publicKey),
            Fingerprint = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(publicKey)),
            NotBefore = notBefore,
            NotAfter = notAfter,
            Revoked = revoked
        };

    internal static byte[] Canonicalize(ReadOnlySpan<byte> json)
    {
        using var document = JsonDocument.Parse(json.ToArray());
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output);
        WriteCanonical(writer, document.RootElement);
        writer.Flush();
        return output.WrittenSpan.ToArray();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                var properties = value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal).ToArray();
                for (var index = 1; index < properties.Length; index++)
                {
                    if (string.Equals(properties[index - 1].Name, properties[index].Name, StringComparison.Ordinal))
                    {
                        throw new JsonException($"Duplicate JSON property '{properties[index].Name}' is not canonical.");
                    }
                }

                foreach (var property in properties)
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray()) WriteCanonical(writer, item);
                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    internal sealed record SigningKey(string KeyId, Ed25519PrivateKeyParameters PrivateKey, byte[] PublicKey);
}
