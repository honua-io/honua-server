// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.FeatureStore.Domain;

/// <summary>
/// Defines the protocol-visible field-name syntax shared by query adapters and
/// providers that quote a resolved field as one storage identifier.
/// </summary>
/// <remarks>
/// This is only a syntax check. Callers must separately resolve the name against
/// the layer's declared schema before using it. Colons, dots, and hyphens are
/// admitted after the first character for extension fields such as
/// <c>eo:cloud_cover</c>; quote characters, whitespace, semicolons, and control
/// characters remain excluded.
/// </remarks>
public static class FeatureFieldNameSyntax
{
    /// <summary>
    /// Determines whether <paramref name="fieldName"/> has the supported field-token shape.
    /// </summary>
    /// <param name="fieldName">The field name to inspect.</param>
    /// <returns><see langword="true"/> when the token has a supported shape.</returns>
    public static bool IsValid(string? fieldName)
    {
        if (string.IsNullOrWhiteSpace(fieldName))
        {
            return false;
        }

        for (var i = 0; i < fieldName.Length; i++)
        {
            var ch = fieldName[i];
            var allowed = char.IsLetterOrDigit(ch)
                || ch == '_'
                || (i > 0 && (ch == ':' || ch == '.' || ch == '-'));
            if (!allowed)
            {
                return false;
            }
        }

        return true;
    }
}
