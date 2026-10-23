// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Numerics;
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
        ValidateGovernedToolTargets(request.Certification!, events);
        var issuedAt = timeProvider.GetUtcNow();
        var requestBytes = Canonicalize(request.AcceptedRequestJson ?? JsonSerializer.SerializeToUtf8Bytes(
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
            writer.WriteString("tenantId", binding.TenantId);
            writer.WriteBase64String("terminalResultDigest", terminalDigest);
            writer.WriteEndObject();
            writer.Flush();
        }

        // Normalize through the same canonicalizer used by every independent
        // verifier. In particular, DateTimeOffset.WriteStringValue and
        // JsonElement.WriteTo encode the UTC offset differently; signing the
        // hand-written buffer directly would make an otherwise valid envelope
        // fail its own canonical-byte check after transport.
        var canonicalTranscript = Canonicalize(buffer.WrittenSpan);
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

    private static void ValidateGovernedToolTargets(
        StudioAiTranscriptCertification certification,
        IReadOnlyList<StudioAiChatEvent> events)
    {
        var toolNamesByCallId = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var evt in events.Where(candidate => candidate.Type == StudioAiChatEventType.ToolCallStart))
        {
            if (string.IsNullOrWhiteSpace(evt.ToolCallId)
                || string.IsNullOrWhiteSpace(evt.ToolName)
                || !toolNamesByCallId.TryAdd(evt.ToolCallId, evt.ToolName))
            {
                throw new InvalidOperationException("Tool call start events must have unique IDs and names.");
            }
        }

        foreach (var evt in events.Where(candidate => candidate.Type == StudioAiChatEventType.ToolCallStop))
        {
            if (string.IsNullOrWhiteSpace(evt.ToolCallId)
                || !toolNamesByCallId.TryGetValue(evt.ToolCallId, out var toolName))
            {
                throw new InvalidOperationException("Tool call stop event does not match a tool call start event.");
            }

            if (string.Equals(
                    toolName,
                    "honua_propose_platform_release_convergence",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Multi-target release convergence is not eligible for candidate-certified transcripts.");
            }

            var targetProperty = toolName switch
            {
                "honua_propose_deploy_operation" => "targetId",
                "honua_propose_deploy_plan" => "targetId",
                "honua_propose_rollback" => "targetId",
                "honua_propose_finding" => "candidateId",
                "honua_propose_metadata_release" => "targetEnvironment",
                _ => null
            };
            if (targetProperty is null)
            {
                continue;
            }

            if (evt.ToolArguments is not { ValueKind: JsonValueKind.Object } arguments
                || !arguments.TryGetProperty(targetProperty, out var target)
                || target.ValueKind != JsonValueKind.String
                || !string.Equals(target.GetString(), certification.CandidateId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Governed tool target does not match the certified candidate.");
            }
        }
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
            case JsonValueKind.String:
                writer.WriteStringValue(value.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(CanonicalizeNumber(value.GetRawText()), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
            default:
                throw new JsonException($"Unsupported JSON value kind '{value.ValueKind}'.");
        }
    }

    private static string CanonicalizeNumber(string raw)
    {
        var negative = raw[0] == '-';
        var unsigned = negative ? raw.AsSpan(1) : raw.AsSpan();
        var exponentIndex = unsigned.IndexOfAny('e', 'E');
        var significand = exponentIndex >= 0 ? unsigned[..exponentIndex] : unsigned;
        var exponent = exponentIndex >= 0
            ? BigInteger.Parse(unsigned[(exponentIndex + 1)..], System.Globalization.CultureInfo.InvariantCulture)
            : BigInteger.Zero;
        var decimalIndex = significand.IndexOf('.');
        var fractionalDigits = decimalIndex >= 0 ? significand.Length - decimalIndex - 1 : 0;
        var digits = decimalIndex >= 0
            ? string.Concat(significand[..decimalIndex], significand[(decimalIndex + 1)..])
            : significand.ToString();

        digits = digits.TrimStart('0');
        if (digits.Length == 0)
        {
            return "0";
        }

        var trailingZeros = digits.Length - digits.TrimEnd('0').Length;
        if (trailingZeros > 0)
        {
            digits = digits[..^trailingZeros];
        }

        exponent = exponent - fractionalDigits + trailingZeros;
        var scientificExponent = exponent + digits.Length - 1;
        var sign = negative ? "-" : string.Empty;
        if (scientificExponent >= -6 && scientificExponent <= 20)
        {
            var decimalPosition = checked((int)(digits.Length + exponent));
            if (decimalPosition <= 0)
            {
                return $"{sign}0.{new string('0', -decimalPosition)}{digits}";
            }

            if (decimalPosition >= digits.Length)
            {
                return $"{sign}{digits}{new string('0', decimalPosition - digits.Length)}";
            }

            return $"{sign}{digits[..decimalPosition]}.{digits[decimalPosition..]}";
        }

        var fraction = digits.Length == 1 ? string.Empty : $".{digits[1..]}";
        return $"{sign}{digits[0]}{fraction}e{scientificExponent}";
    }

    internal sealed record SigningKey(string KeyId, Ed25519PrivateKeyParameters PrivateKey, byte[] PublicKey);
}
