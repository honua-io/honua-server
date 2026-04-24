// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Spec.Domain;

/// <summary>
/// Constants describing the shipped spec grammar and operator-catalog versions.
/// The grammar version is the north star for spec-language compatibility; the
/// operator-catalog version is tracked separately so that non-breaking operator
/// additions do not require a grammar bump.
/// </summary>
public static class SpecGrammarVersion
{
    /// <summary>
    /// Current grammar major.minor version. Specs pinned to a version newer
    /// than this value are rejected during validation.
    /// </summary>
    public const string Current = "v1.0";

    /// <summary>
    /// Current major version. Specs on a different major require a migration
    /// step (out of scope for S1).
    /// </summary>
    public const int CurrentMajor = 1;

    /// <summary>
    /// Current minor version.
    /// </summary>
    public const int CurrentMinor = 0;

    /// <summary>
    /// Current operator-catalog capability version. Versioned independently so
    /// that non-breaking operator additions do not force a grammar bump.
    /// </summary>
    public const string CurrentOperatorCapability = "v1.0";

    /// <summary>
    /// JSON Schema URL advertised at the top of canonical specs.
    /// </summary>
    public const string SchemaUrl = "https://honua.io/spec/grammar/v1.0/spec.json";

    /// <summary>
    /// Tries to parse a grammar version string of the form <c>vMAJOR.MINOR</c>.
    /// </summary>
    /// <param name="value">Version string.</param>
    /// <param name="major">Parsed major version.</param>
    /// <param name="minor">Parsed minor version.</param>
    /// <returns><c>true</c> when the string is syntactically valid.</returns>
    public static bool TryParse(string? value, out int major, out int minor)
    {
        major = 0;
        minor = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.AsSpan().Trim();
        if (trimmed.Length < 4 || (trimmed[0] != 'v' && trimmed[0] != 'V'))
        {
            return false;
        }

        var body = trimmed[1..];
        var dot = body.IndexOf('.');
        if (dot <= 0 || dot == body.Length - 1)
        {
            return false;
        }

        return int.TryParse(body[..dot], out major) && int.TryParse(body[(dot + 1)..], out minor);
    }
}
