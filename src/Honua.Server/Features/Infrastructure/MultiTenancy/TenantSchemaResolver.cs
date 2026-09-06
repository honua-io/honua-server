// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.MultiTenancy;

/// <summary>
/// Resolves exact tenant identifiers without lossy normalization or PostgreSQL truncation.
/// Explicit mappings reserve their schema names against all other tenants.
/// </summary>
internal sealed class TenantSchemaResolver : ITenantSchemaResolver
{
    private readonly TenantSchemaOptions _options;
    private readonly Dictionary<string, string> _schemaMap;
    private readonly HashSet<string> _conflictingSchemas;
    private readonly HashSet<string> _reservedSchemas;

    public TenantSchemaResolver(IOptions<TenantSchemaOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value ?? throw new ArgumentNullException(nameof(options));
        // Configuration dictionaries may use a case-insensitive comparer. Copy the actual
        // keys so a claim with different casing cannot inherit somebody else's mapping.
        _schemaMap = new(StringComparer.Ordinal);
        _reservedSchemas = new(StringComparer.OrdinalIgnoreCase);
        _conflictingSchemas = new(StringComparer.OrdinalIgnoreCase);
        var owners = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var mappings = _options.SchemaMap.Concat(_options.SchemaMappings.Select(mapping =>
            new KeyValuePair<string, string>(mapping.TenantId, mapping.SchemaName)));
        foreach (var (tenant, schema) in mappings)
        {
            if (!_schemaMap.TryAdd(tenant, schema)
                && !string.Equals(_schemaMap[tenant], schema, StringComparison.Ordinal))
            {
                // Conflicting declarations cannot be resolved by configuration order.
                _schemaMap[tenant] = string.Empty;
            }

            _reservedSchemas.Add(schema);
            if (!owners.TryAdd(schema, tenant)
                && !string.Equals(owners[schema], tenant, StringComparison.Ordinal))
            {
                _conflictingSchemas.Add(schema);
            }
        }
    }

    /// <inheritdoc />
    public bool TryResolveSchema(string tenantId, out string schemaName)
    {
        schemaName = string.Empty;
        if (string.IsNullOrWhiteSpace(tenantId)
            || !string.Equals(tenantId, tenantId.Trim(), StringComparison.Ordinal)
            || _options.UnroutedTenantIds.Contains(tenantId, StringComparer.Ordinal))
        {
            return false;
        }

        if (_schemaMap.TryGetValue(tenantId, out var mapped))
        {
            if (!IsSafeIdentifier(mapped) || _conflictingSchemas.Contains(mapped))
            {
                return false;
            }

            schemaName = mapped;
            return true;
        }

        // Compatibility mode retains only identities the old normalizer did not change.
        // Other existing tenants need a verified SchemaMap, never a silent schema move.
        if (!_options.UseEncodedSchemaNames && !tenantId.All(IsCanonicalCharacter))
        {
            return false;
        }

        var derived = _options.SchemaPrefix
            + (_options.UseEncodedSchemaNames ? Encode(tenantId) : tenantId);
        if (!IsSafeIdentifier(derived) || _reservedSchemas.Contains(derived))
        {
            return false;
        }

        schemaName = derived;
        return true;
    }

    private static string Encode(string tenantId)
    {
        var builder = new StringBuilder(tenantId.Length);
        foreach (var character in tenantId)
        {
            if (IsCanonicalCharacter(character) && character != '_')
            {
                builder.Append(character);
            }
            else
            {
                // Escape the escape character too: distinct input strings cannot share an
                // encoding. UTF-16 code units preserve even non-ASCII identities exactly.
                builder.Append('_').Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
            }
        }

        return builder.ToString();
    }

    private static bool IsCanonicalCharacter(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_';

    private static bool IsSafeIdentifier(string value)
    {
        // All allowed characters are ASCII, so length is also the PostgreSQL byte length.
        if (string.IsNullOrEmpty(value) || value.Length > 63
            || value[0] is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or '_'))
        {
            return false;
        }

        return value.All(character => IsCanonicalCharacter(character) || character is >= 'A' and <= 'Z');
    }
}
