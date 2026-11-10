// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Exceptions;

namespace Honua.Core.Features.Operations.Domain;

// The wire value is urn:honua:provisioning: plus 32 lowercase hex digits; two different roots are rejected, and this type never mints an identity.
public static class RootProvisioningOperationIds
{
    public const string FieldName = "rootProvisioningOperationId";

    private const string Prefix = "urn:honua:provisioning:";

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

    public static string? FromParameters(IReadOnlyDictionary<string, string?>? parameters)
    {
        if (parameters is null || !parameters.TryGetValue(FieldName, out var value))
        {
            return null;
        }

        return Normalize(value);
    }

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

    public static string? PreferCanonical(params string?[] candidates)
    {
        string? chosen = null;
        foreach (var candidate in candidates)
        {
            chosen = PreferCanonical(chosen, candidate);
        }

        return chosen;
    }

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
