// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// A declaration, stored as an ordinary audit row, that one contiguous run of unhashed
/// rows was appended by a writer that predates the hash chain (a binary downgrade).
/// The verifier accepts the declared rows as an attested gap only when the declaration
/// matches the stored rows exactly and the declaration row itself verifies as a keyed MAC.
/// Existing rows are never rewritten.
/// </summary>
public sealed record AuditChainGapAttestation
{
    /// <summary>The <c>action</c> of an attestation row.</summary>
    public const string Action = "audit.chain.gap_attested";

    /// <summary>The <c>resource_type</c> of an attestation row.</summary>
    public const string ResourceType = "audit-chain";

    /// <summary>The <c>schema</c> member of the attestation details document.</summary>
    public const string Schema = "honua.audit-chain-gap/v1";

    /// <summary>The <c>audit_id</c> of the attestation row itself.</summary>
    public long AttestationAuditId { get; init; }

    /// <summary>The last hashed row before the gap.</summary>
    public required long AnchorAuditId { get; init; }

    /// <summary>The stored <c>entry_hash</c> of <see cref="AnchorAuditId"/>.</summary>
    public required string AnchorEntryHash { get; init; }

    /// <summary>The first unhashed row of the gap.</summary>
    public required long GapFirstAuditId { get; init; }

    /// <summary>The last unhashed row of the gap.</summary>
    public required long GapLastAuditId { get; init; }

    /// <summary>The number of rows in the gap.</summary>
    public required long GapRowCount { get; init; }

    /// <summary>The first hashed row after the gap.</summary>
    public required long ResumeAuditId { get; init; }

    /// <summary>
    /// The stored <c>prev_hash</c> of <see cref="ResumeAuditId"/>: <c>null</c> when the
    /// resuming writer started a new chain, or <see cref="AnchorEntryHash"/> when it
    /// linked over the gap.
    /// </summary>
    public string? ResumePreviousHash { get; init; }

    /// <summary>The operator's stated reason.</summary>
    public required string Reason { get; init; }

    /// <summary>Serializes the declaration as the attestation row's <c>details</c>.</summary>
    public string ToDetails()
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", Schema);
            writer.WriteNumber("anchorAuditId", AnchorAuditId);
            writer.WriteString("anchorEntryHash", AnchorEntryHash);
            writer.WriteNumber("gapFirstAuditId", GapFirstAuditId);
            writer.WriteNumber("gapLastAuditId", GapLastAuditId);
            writer.WriteNumber("gapRowCount", GapRowCount);
            writer.WriteNumber("resumeAuditId", ResumeAuditId);
            if (ResumePreviousHash is null)
            {
                writer.WriteNull("resumePreviousHash");
            }
            else
            {
                writer.WriteString("resumePreviousHash", ResumePreviousHash);
            }

            writer.WriteString("reason", Reason);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Parses an attestation row. Returns <c>null</c> for a row that is not a
    /// well-formed, internally consistent declaration; such a row excuses nothing.
    /// </summary>
    /// <param name="attestationAuditId">The <c>audit_id</c> of the row.</param>
    /// <param name="details">The row's <c>details</c>.</param>
    public static AuditChainGapAttestation? TryParse(long attestationAuditId, string? details)
    {
        if (string.IsNullOrWhiteSpace(details))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(details);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryGetString(root, "schema", out var schema)
                || !string.Equals(schema, Schema, StringComparison.Ordinal)
                || !TryGetInt64(root, "anchorAuditId", out var anchorAuditId)
                || !TryGetString(root, "anchorEntryHash", out var anchorEntryHash)
                || !TryGetInt64(root, "gapFirstAuditId", out var gapFirst)
                || !TryGetInt64(root, "gapLastAuditId", out var gapLast)
                || !TryGetInt64(root, "gapRowCount", out var gapRowCount)
                || !TryGetInt64(root, "resumeAuditId", out var resumeAuditId)
                || !root.TryGetProperty("resumePreviousHash", out var resumePrevious)
                || !TryGetString(root, "reason", out var reason))
            {
                return null;
            }

            string? resumePreviousHash = resumePrevious.ValueKind switch
            {
                JsonValueKind.Null => null,
                JsonValueKind.String => resumePrevious.GetString(),
                _ => string.Empty,
            };

            var attestation = new AuditChainGapAttestation
            {
                AttestationAuditId = attestationAuditId,
                AnchorAuditId = anchorAuditId,
                AnchorEntryHash = anchorEntryHash,
                GapFirstAuditId = gapFirst,
                GapLastAuditId = gapLast,
                GapRowCount = gapRowCount,
                ResumeAuditId = resumeAuditId,
                ResumePreviousHash = resumePreviousHash,
                Reason = reason,
            };

            return attestation.IsConsistent() ? attestation : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private bool IsConsistent()
        => AnchorEntryHash.Length > 0
           && AnchorAuditId < GapFirstAuditId
           && GapFirstAuditId <= GapLastAuditId
           && GapRowCount >= 1
           && GapRowCount <= GapLastAuditId - GapFirstAuditId + 1
           && GapLastAuditId < ResumeAuditId
           && ResumeAuditId < AttestationAuditId
           && (ResumePreviousHash is null
               || string.Equals(ResumePreviousHash, AnchorEntryHash, StringComparison.Ordinal));

    private static bool TryGetString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = element.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static bool TryGetInt64(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
               && element.ValueKind == JsonValueKind.Number
               && element.TryGetInt64(out value);
    }

    /// <summary>The attestation row's <c>resource_id</c>.</summary>
    public string ResourceId
        => string.Create(CultureInfo.InvariantCulture, $"gap:{GapFirstAuditId}-{GapLastAuditId}");
}
