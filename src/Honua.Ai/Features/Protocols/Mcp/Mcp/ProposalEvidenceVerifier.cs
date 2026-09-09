// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Honua.Ai.Protocols.Mcp.Models;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.Ai.StudioAiProxy;
using Honua.Ai.StudioAiProxy.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Geoprocessing;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

namespace Honua.Ai.Protocols.Mcp;

/// <summary>
/// Independently verifies that a model-selected mutation is the exact catalog-owned
/// proposal call signed by the production Studio proxy. The verified values are then
/// persisted with the proposal and revalidated by the approval boundary.
/// </summary>
internal sealed class ProposalEvidenceVerifier(
    StudioAiTranscriptSigner signer,
    TimeProvider timeProvider)
{
    internal const string HttpContextItemKey = "honua.mcp.proposal-evidence.verified";
    internal const string MetaProperty = "honua.io/proposal-evidence";
    internal const string PolicyRevision = "separate-human-approval/v1";

    public async Task<OperationProposalEvidence> VerifyAsync(
        IMcpTool tool,
        JsonElement? arguments,
        JsonElement? meta,
        string tenantId,
        string sessionId,
        JsonElement? callId,
        CancellationToken cancellationToken)
    {
        if (tool is not IEvidenceBoundProposalTool proposalTool)
        {
            throw Invalid("Signed model output may select only a catalog-owned proposal tool.");
        }

        if (arguments is not { ValueKind: JsonValueKind.Object } invocation
            || meta is not { ValueKind: JsonValueKind.Object } metadata
            || !metadata.TryGetProperty(MetaProperty, out var evidenceElement)
            || evidenceElement.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("A signed proposal-evidence envelope is required for this tool.");
        }

        StudioAiSignedTranscript signed;
        try
        {
            signed = JsonSerializer.Deserialize(
                evidenceElement,
                StudioAiProxyJsonContext.Default.StudioAiSignedTranscript)
                ?? throw new JsonException("Missing signed transcript.");
        }
        catch (JsonException ex)
        {
            throw Invalid($"The proposal-evidence envelope is invalid: {ex.Message}");
        }

        if (!string.Equals(signed.SchemaVersion, "honua.studio-ai.transcript.v1", StringComparison.Ordinal)
            || !string.Equals(signed.Canonicalization, "honua-canonical-json-v1", StringComparison.Ordinal)
            || !string.Equals(signed.DigestAlgorithm, "sha-256", StringComparison.Ordinal)
            || !string.Equals(signed.SignatureAlgorithm, "Ed25519", StringComparison.Ordinal))
        {
            throw Invalid("The proposal-evidence algorithms or schema are unsupported.");
        }

        byte[] transcriptBytes;
        byte[] signatureBytes;
        try
        {
            transcriptBytes = Convert.FromBase64String(signed.CanonicalTranscript);
            signatureBytes = Convert.FromBase64String(signed.Signature);
        }
        catch (FormatException)
        {
            throw Invalid("The proposal-evidence encoding is invalid.");
        }

        var canonical = StudioAiTranscriptSigner.Canonicalize(transcriptBytes);
        if (!CryptographicOperations.FixedTimeEquals(canonical, transcriptBytes)
            || !FixedTimeHexEquals(signed.TranscriptDigest, SHA256.HashData(transcriptBytes)))
        {
            throw Invalid("The canonical transcript digest does not match.");
        }

        var now = timeProvider.GetUtcNow();
        var manifest = await signer.GetManifestAsync(cancellationToken).ConfigureAwait(false);
        var key = manifest.Keys.SingleOrDefault(candidate =>
            string.Equals(candidate.KeyId, signed.KeyId, StringComparison.Ordinal));
        if (key is null || key.Revoked || key.NotBefore > now || key.NotAfter <= now)
        {
            throw Invalid("The transcript signing key is unknown, revoked, or outside its verification window.");
        }

        byte[] publicKey;
        try { publicKey = Convert.FromBase64String(key.PublicKey); }
        catch (FormatException) { throw Invalid("The transcript verification key is invalid."); }
        if (publicKey.Length != Ed25519PublicKeyParameters.KeySize
            || signatureBytes.Length != 64
            || !VerifySignature(publicKey, transcriptBytes, signatureBytes))
        {
            throw Invalid("The transcript signature is invalid.");
        }

        using var transcript = JsonDocument.Parse(transcriptBytes);
        var root = transcript.RootElement;
        RequireString(root, "schemaVersion", "honua.studio-ai.transcript.v1");
        var issuedAt = RequireDate(root, "issuedAt");
        var expiresAt = RequireDate(root, "expiresAt");
        if (issuedAt > now || expiresAt <= now || expiresAt <= issuedAt)
        {
            throw Invalid("The signed transcript is not currently valid.");
        }

        var candidateId = RequireString(root, "candidateId");
        var transcriptTenant = RequireString(root, "tenantId");
        if (!string.Equals(transcriptTenant, tenantId, StringComparison.Ordinal))
        {
            throw Invalid("The signed transcript tenant does not match the authenticated MCP tenant.");
        }

        var requestBytes = RequireBase64(root, "request");
        if (!CryptographicOperations.FixedTimeEquals(
                StudioAiTranscriptSigner.Canonicalize(requestBytes), requestBytes))
        {
            throw Invalid("The signed request is not canonical.");
        }

        var request = JsonSerializer.Deserialize(
            requestBytes,
            StudioAiProxyJsonContext.Default.StudioAiChatRequest)
            ?? throw Invalid("The signed request is missing.");
        if (request.Certification is null
            || !string.Equals(request.Certification.CandidateId, candidateId, StringComparison.Ordinal)
            || !string.Equals(request.Certification.TenantId, transcriptTenant, StringComparison.Ordinal)
            || request.ToolChoice?.Mode != StudioAiToolChoiceMode.Specific
            || !string.Equals(request.ToolChoice.ToolName, tool.Name, StringComparison.Ordinal))
        {
            throw Invalid("The signed request certification or forced tool selection does not match.");
        }

        var advertised = request.Tools?.Where(candidate =>
            string.Equals(candidate.Name, tool.Name, StringComparison.Ordinal)).ToArray() ?? [];
        var descriptor = tool.Describe();
        if (advertised.Length != 1
            || !CanonicalEquals(advertised[0].InputSchema, descriptor.InputSchema))
        {
            throw Invalid("The signed request does not contain the current catalog-owned tool descriptor.");
        }

        ValidateClosedSchema(descriptor.InputSchema, invocation);

        var eventBytes = RequireBase64(root, "providerEvents");
        var terminalDigest = RequireBase64(root, "terminalResultDigest");
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(eventBytes), terminalDigest))
        {
            throw Invalid("The signed provider-event digest does not match.");
        }

        var events = JsonSerializer.Deserialize(
            eventBytes,
            StudioAiProxyJsonContext.Default.ListStudioAiChatEvent)
            ?? throw Invalid("The signed provider events are missing.");
        var starts = events.Where(candidate => candidate.Type == StudioAiChatEventType.ToolCallStart).ToArray();
        var stops = events.Where(candidate => candidate.Type == StudioAiChatEventType.ToolCallStop).ToArray();
        if (starts.Length != 1 || stops.Length != 1
            || !string.Equals(starts[0].ToolCallId, stops[0].ToolCallId, StringComparison.Ordinal)
            || !string.Equals(starts[0].ToolName, tool.Name, StringComparison.Ordinal)
            || stops[0].ToolArguments is not { ValueKind: JsonValueKind.Object } signedArguments
            || !CanonicalEquals(signedArguments, invocation)
            || events.LastOrDefault()?.Type != StudioAiChatEventType.MessageStop
            || events.Last().StopReason != StudioAiStopReason.ToolCall)
        {
            throw Invalid("The signed model output does not select the exact MCP proposal invocation.");
        }

        var targetId = RequireString(invocation, proposalTool.TargetProperty);
        if (!string.Equals(targetId, candidateId, StringComparison.Ordinal))
        {
            throw Invalid("The proposal target does not match the certified candidate.");
        }

        var canonicalRequest = StudioAiTranscriptSigner.Canonicalize(
            Encoding.UTF8.GetBytes(invocation.GetRawText()));
        return new OperationProposalEvidence
        {
            ToolName = tool.Name,
            OperationId = proposalTool.OperationId,
            CandidateId = candidateId,
            TenantId = transcriptTenant,
            TargetId = targetId,
            DescriptorRevision = ComputeDescriptorRevision(descriptor),
            PolicyRevision = PolicyRevision,
            AuthorizationDecision = "pending-call-authorization",
            RequestDigest = Convert.ToHexStringLower(SHA256.HashData(canonicalRequest)),
            CanonicalRequest = Convert.ToBase64String(canonicalRequest),
            // The server adapter replaces these request placeholders with the canonical
            // protocol-neutral execution payload before the gateway persists the proposal.
            PayloadDigest = Convert.ToHexStringLower(SHA256.HashData(canonicalRequest)),
            CanonicalPayload = Convert.ToBase64String(canonicalRequest),
            TranscriptDigest = signed.TranscriptDigest,
            TranscriptKeyId = signed.KeyId,
            CanonicalTranscript = signed.CanonicalTranscript,
            TranscriptSignature = signed.Signature,
            ReleaseId = RequireString(root, "releaseId"),
            ActionId = RequireString(root, "actionId"),
            RunNonce = RequireString(root, "runNonce"),
            McpSessionId = sessionId,
            McpCallId = callId?.GetRawText() ?? "null",
            IssuedAt = issuedAt,
            ExpiresAt = expiresAt,
        };
    }

    internal static string ComputeDescriptorRevision(McpToolDescriptor descriptor)
    {
        var schema = StudioAiTranscriptSigner.Canonicalize(
            Encoding.UTF8.GetBytes(descriptor.InputSchema.GetRawText()));
        var name = Encoding.UTF8.GetBytes(descriptor.Name);
        var material = new byte[name.Length + 1 + schema.Length];
        name.CopyTo(material, 0);
        schema.CopyTo(material, name.Length + 1);
        return Convert.ToHexStringLower(SHA256.HashData(material));
    }

    private static void ValidateClosedSchema(JsonElement schema, JsonElement arguments)
    {
        if (schema.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("type", out var type)
            || type.GetString() != "object"
            || !schema.TryGetProperty("properties", out var properties)
            || properties.ValueKind != JsonValueKind.Object
            || !schema.TryGetProperty("additionalProperties", out var additional)
            || additional.ValueKind != JsonValueKind.False)
        {
            throw Invalid("The live proposal descriptor is unsupported because it is not a closed object schema.");
        }

        var allowed = properties.EnumerateObject().ToDictionary(item => item.Name, item => item.Value, StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!allowed.TryGetValue(property.Name, out var propertySchema))
            {
                throw Invalid($"Invocation property '{property.Name}' is not present in the catalog schema.");
            }

            if (propertySchema.TryGetProperty("type", out var expectedType)
                && !MatchesType(property.Value, expectedType.GetString()))
            {
                throw Invalid($"Invocation property '{property.Name}' has the wrong JSON type.");
            }
        }

        if (schema.TryGetProperty("required", out var required))
        {
            foreach (var name in required.EnumerateArray().Select(item => item.GetString()!))
            {
                if (!arguments.TryGetProperty(name, out _))
                {
                    throw Invalid($"Invocation is missing required property '{name}'.");
                }
            }
        }
    }

    private static bool MatchesType(JsonElement value, string? type) => type switch
    {
        "string" => value.ValueKind == JsonValueKind.String,
        "integer" => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out _),
        "number" => value.ValueKind == JsonValueKind.Number,
        "boolean" => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
        "object" => value.ValueKind == JsonValueKind.Object,
        "array" => value.ValueKind == JsonValueKind.Array,
        _ => false,
    };

    private static bool CanonicalEquals(JsonElement left, JsonElement right)
        => CryptographicOperations.FixedTimeEquals(
            StudioAiTranscriptSigner.Canonicalize(Encoding.UTF8.GetBytes(left.GetRawText())),
            StudioAiTranscriptSigner.Canonicalize(Encoding.UTF8.GetBytes(right.GetRawText())));

    private static bool VerifySignature(byte[] key, byte[] message, byte[] signature)
    {
        var verifier = new Ed25519Signer();
        verifier.Init(false, new Ed25519PublicKeyParameters(key, 0));
        verifier.BlockUpdate(message, 0, message.Length);
        return verifier.VerifySignature(signature);
    }

    private static bool FixedTimeHexEquals(string actual, byte[] expected)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromHexString(actual), expected); }
        catch (FormatException) { return false; }
    }

    private static byte[] RequireBase64(JsonElement owner, string name)
    {
        try { return Convert.FromBase64String(RequireString(owner, name)); }
        catch (FormatException) { throw Invalid($"Transcript property '{name}' is not valid base64."); }
    }

    private static DateTimeOffset RequireDate(JsonElement owner, string name)
        => owner.TryGetProperty(name, out var value) && value.TryGetDateTimeOffset(out var result)
            ? result
            : throw Invalid($"Transcript property '{name}' is missing or invalid.");

    private static string RequireString(JsonElement owner, string name, string? expected = null)
    {
        if (!owner.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw Invalid($"Transcript property '{name}' is missing or invalid.");
        }

        var result = value.GetString()!;
        if (expected is not null && !string.Equals(result, expected, StringComparison.Ordinal))
        {
            throw Invalid($"Transcript property '{name}' is unsupported.");
        }

        return result;
    }

    private static GeoprocessingValidationException Invalid(string message) => new(message);
}
