// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Exceptions;

namespace Honua.Core.Features.Operations.Domain;

/// <summary>
/// Canonical provisioning-root identity taken from a verified DevOps handoff.
/// The wire value is <c>urn:honua:provisioning:</c> plus 32 lowercase hex digits.
/// A parameter copy is only a fallback when the canonical field is absent.
/// Two different roots are rejected; this type never mints an identity.
/// </summary>
public static class RootProvisioningOperationIds
{
    /// <summary>JSON and parameter name of the canonical field.</summary>
    public const string FieldName = "rootProvisioningOperationId";

    private const string Prefix = "urn:honua:provisioning:";

    /// <summary>
    /// Returns a trimmed handoff id, or <see langword="null"/> when <paramref name="value"/> is blank.
    /// </summary>
    /// <param name="value">Caller-supplied root, which may be absent.</param>
    /// <returns>The normalized id.</returns>
    /// <exception cref="ArgumentException">The value is present but is not a verified handoff id.</exception>
    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length != Prefix.Length + 32 || !trimmed.StartsWith(Prefix, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "rootProvisioningOperationId must be a verified handoff id (urn:honua:provisioning: and 32 lowercase hex digits).");
        }

        for (var i = Prefix.Length; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (c is < '0' or > '9' && c is < 'a' or > 'f')
            {
                throw new ArgumentException(
                    "rootProvisioningOperationId must be a verified handoff id (urn:honua:provisioning: and 32 lowercase hex digits).");
            }
        }

        return trimmed;
    }

    /// <summary>Reads the deploy-parameter fallback, when the caller sent one.</summary>
    /// <param name="parameters">Caller parameter map. A missing key is an absent root.</param>
    /// <returns>The normalized parameter root, or <see langword="null"/>.</returns>
    public static string? FromParameters(IReadOnlyDictionary<string, string?>? parameters)
    {
        if (parameters is null || !parameters.TryGetValue(FieldName, out var value))
        {
            return null;
        }

        return Normalize(value);
    }

    /// <summary>
    /// Prefers <paramref name="canonical"/> when it is present and otherwise uses
    /// <paramref name="fallback"/>. A disagreement is rejected.
    /// </summary>
    /// <param name="canonical">Canonical field, which wins when present.</param>
    /// <param name="fallback">Deploy-parameter (or other nested) copy used only when the canonical field is absent.</param>
    /// <returns>The single accepted root, or <see langword="null"/> when both are absent.</returns>
    /// <exception cref="ArgumentException">The values are present and differ, or either value is malformed.</exception>
    public static string? PreferCanonical(string? canonical, string? fallback)
    {
        canonical = Normalize(canonical);
        fallback = Normalize(fallback);
        if (canonical is not null && fallback is not null && !string.Equals(canonical, fallback, StringComparison.Ordinal))
        {
            throw new ArgumentException("The request substitutes a different provisioning root.");
        }

        return canonical ?? fallback;
    }

    /// <summary>
    /// Prefers the first present candidate and rejects any later candidate that names a different root.
    /// </summary>
    /// <param name="candidates">Roots in preference order. Null entries are skipped.</param>
    /// <returns>The preferred root, or <see langword="null"/> when every candidate is absent.</returns>
    public static string? PreferCanonical(params string?[] candidates)
    {
        string? chosen = null;
        foreach (var candidate in candidates)
        {
            chosen = PreferCanonical(chosen, candidate);
        }

        return chosen;
    }

    /// <summary>
    /// Rejects a replay that names a different root than the one already stamped, including a
    /// first root attached to a record that was accepted without one.
    /// An omitted supplied root leaves the recorded root unchanged.
    /// </summary>
    /// <param name="recorded">Root already stamped on the durable record.</param>
    /// <param name="supplied">Root on the replay, or <see langword="null"/> when the replay does not assert one.</param>
    /// <exception cref="ResourceConflictException">The replay substitutes a different provisioning root.</exception>
    public static void RejectRelabel(string? recorded, string? supplied)
    {
        supplied = Normalize(supplied);
        if (supplied is null)
        {
            return;
        }

        recorded = Normalize(recorded);
        if (!string.Equals(recorded, supplied, StringComparison.Ordinal))
        {
            throw new ResourceConflictException("The request substitutes a different provisioning root.");
        }
    }

    /// <summary>
    /// Aligns a supplied handoff root with a root already stamped on a related record.
    /// The recorded root wins when the caller omits one. A recorded root of
    /// <see langword="null"/> accepts the supplied root. Disagreement is rejected.
    /// </summary>
    /// <param name="recorded">Root already stamped on the related record.</param>
    /// <param name="supplied">Root supplied with this request.</param>
    /// <returns>The root to stamp on the new record.</returns>
    /// <exception cref="ResourceConflictException">Both roots are present and differ.</exception>
    public static string? AlignWithRecord(string? recorded, string? supplied)
    {
        supplied = Normalize(supplied);
        recorded = Normalize(recorded);
        if (recorded is null)
        {
            return supplied;
        }

        if (supplied is not null && !string.Equals(recorded, supplied, StringComparison.Ordinal))
        {
            throw new ResourceConflictException("The request substitutes a different provisioning root.");
        }

        return recorded;
    }
}
