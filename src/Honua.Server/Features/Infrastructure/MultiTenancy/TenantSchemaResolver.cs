// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System;
using System.Text;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Default <see cref="ITenantSchemaResolver"/>: derives a SQL-safe PostgreSQL schema name
/// from a tenant id, honoring explicit overrides and an exclusion list (issue #346).
/// </summary>
/// <remarks>
/// Resolution order:
///  1. <see cref="TenantSchemaOptions.UnroutedTenantIds"/> — excluded tenants resolve to
///     <see langword="false"/> so the connection keeps its configured default schema.
///  2. <see cref="TenantSchemaOptions.SchemaMap"/> — explicit tenant-id -&gt; schema overrides.
///  3. Derived form <c>{SchemaPrefix}{normalized-tenant-id}</c>, where normalization lower-cases
///     the id and replaces every character outside <c>[a-z0-9_]</c> with <c>_</c>.
///
/// Every resolved schema name is validated against the same safe-identifier allow-list used
/// by the database search-path applier, so a tenant id can never inject SQL via the schema.
/// </remarks>
internal sealed class TenantSchemaResolver : ITenantSchemaResolver
{
    private readonly TenantSchemaOptions _options;

    public TenantSchemaResolver(IOptions<TenantSchemaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    public bool TryResolveSchema(string tenantId, out string schemaName)
    {
        schemaName = string.Empty;
        if (string.IsNullOrWhiteSpace(tenantId))
        {
            return false;
        }

        var trimmed = tenantId.Trim();

        // 1. Excluded tenants keep the default schema (e.g. the anonymous public tenant).
        if (_options.UnroutedTenantIds.Any(unrouted =>
                !string.IsNullOrWhiteSpace(unrouted)
                && string.Equals(unrouted.Trim(), trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        // 2. Explicit override map takes precedence over the derived form.
        if (_options.SchemaMap.TryGetValue(trimmed, out var mapped) && IsSafeIdentifier(mapped))
        {
            schemaName = mapped.Trim();
            return true;
        }

        // 3. Derived form: {prefix}{normalized-tenant-id}.
        var prefix = _options.SchemaPrefix ?? string.Empty;
        var derived = prefix + Normalize(trimmed);
        if (!IsSafeIdentifier(derived))
        {
            return false;
        }

        schemaName = derived;
        return true;
    }

    // Lower-case and replace any character outside [a-z0-9_] with '_'. Tenant ids may contain
    // '-', '.', ':', '@' (allowed by the tenant rail) which are not valid in unquoted SQL
    // identifiers, so they are folded to '_'. The prefix guarantees the result starts with a
    // letter or underscore even when the tenant id begins with a digit.
    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        // Not a pure map: each mapped character is appended directly into the StringBuilder
        // rather than collected into a new sequence, so a '.Select(...)' rewrite would only add
        // an intermediate allocation without changing behavior.
        foreach (var ch in value)
        {
            var lower = char.ToLowerInvariant(ch);
            var ok = (lower >= 'a' && lower <= 'z')
                || (lower >= '0' && lower <= '9')
                || lower == '_';
            builder.Append(ok ? lower : '_');
        }

        return builder.ToString();
    }

    // Mirrors the ^[A-Za-z_][A-Za-z0-9_]*$ allow-list enforced by the search-path applier.
    private static bool IsSafeIdentifier(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        var first = trimmed[0];
        if (!((first >= 'A' && first <= 'Z') || (first >= 'a' && first <= 'z') || first == '_'))
        {
            return false;
        }

        for (var i = 1; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            var ok = (c >= 'A' && c <= 'Z')
                || (c >= 'a' && c <= 'z')
                || (c >= '0' && c <= '9')
                || c == '_';
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }
}
