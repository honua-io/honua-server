// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Server.Tests.Infrastructure.Security;

/// <summary>
/// Reads the repository's shipped declarative files and reports the credential-shaped
/// literal values they assign, so a drift test can assert each one is already refused by
/// production startup validation.
/// </summary>
/// <remarks>
/// Scope is deliberately limited to declarative <c>key: value</c> / <c>key=value</c>
/// assignments in compose files, <c>.env*.example</c> files, workflow files and composite
/// action files. Shell scripts assign the same values through too many forms to scan
/// reliably; they reuse the literals these files already declare.
/// </remarks>
internal static partial class ShippedSecretLiteralScanner
{
    /// <summary>A credential-shaped literal found in a shipped file.</summary>
    /// <param name="Value">The literal value as written.</param>
    /// <param name="Key">The configuration or environment key it is assigned to.</param>
    /// <param name="Location">Repository-relative <c>path:line</c> of the assignment.</param>
    internal sealed record Finding(string Value, string Key, string Location);

    /// <summary>Repository-root file patterns that declare shipped configuration.</summary>
    private static readonly string[] RootFilePatterns = ["docker-compose*.yml", "compose*.yml", ".env*.example"];

    /// <summary>File patterns scanned recursively under <c>docker/</c>.</summary>
    private static readonly string[] DockerFilePatterns = ["compose*.yml", "docker-compose*.yml", ".env*.example"];

    /// <summary>
    /// Key-name fragments that mark an assignment as credential-shaped. Matched
    /// case-insensitively, so both <c>MasterKey</c> and <c>MASTER_KEY</c> are covered.
    /// </summary>
    private static readonly string[] CredentialKeyFragments =
    [
        "password",
        "passwd",
        "masterkey",
        "master_key",
        "apikey",
        "api_key",
        "secretaccesskey",
        "secret_access_key",
        "signingkey",
        "signing_key",
        "salt",
    ];

    /// <summary>
    /// Keys whose name matches <see cref="CredentialKeyFragments"/> but whose value is not a
    /// credential. Each entry is a deliberate, reviewed exemption.
    /// </summary>
    private static readonly HashSet<string> NonCredentialKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        // PostgreSQL server setting: names the password hashing algorithm, not a password.
        "POSTGRESQL_CONF_password_encryption",
    };

    /// <summary>Scans the repository and returns every credential-shaped literal it declares.</summary>
    /// <param name="repositoryRoot">Absolute path to the repository root.</param>
    /// <returns>One finding per assignment; the same value may appear more than once.</returns>
    internal static IReadOnlyList<Finding> Scan(string repositoryRoot)
    {
        var findings = new List<Finding>();
        foreach (var file in EnumerateShippedFiles(repositoryRoot))
        {
            var relative = Path.GetRelativePath(repositoryRoot, file).Replace('\\', '/');
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (!TryReadAssignment(lines[index], out var key, out var value))
                {
                    continue;
                }

                if (!IsCredentialKey(key) || !TryReadLiteral(value, out var literal))
                {
                    continue;
                }

                findings.Add(new Finding(literal, key, $"{relative}:{index + 1}"));
            }
        }

        return findings;
    }

    private static IEnumerable<string> EnumerateShippedFiles(string repositoryRoot)
    {
        foreach (var pattern in RootFilePatterns)
        {
            foreach (var file in Directory.EnumerateFiles(repositoryRoot, pattern, SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }
        }

        var dockerRoot = Path.Join(repositoryRoot, "docker");
        if (Directory.Exists(dockerRoot))
        {
            foreach (var pattern in DockerFilePatterns)
            {
                foreach (var file in Directory.EnumerateFiles(dockerRoot, pattern, SearchOption.AllDirectories))
                {
                    yield return file;
                }
            }
        }

        var workflowRoot = Path.Join(repositoryRoot, ".github", "workflows");
        if (Directory.Exists(workflowRoot))
        {
            foreach (var file in Directory.EnumerateFiles(workflowRoot, "*.yml", SearchOption.TopDirectoryOnly))
            {
                yield return file;
            }
        }

        var actionRoot = Path.Join(repositoryRoot, ".github", "actions");
        if (Directory.Exists(actionRoot))
        {
            foreach (var file in Directory.EnumerateFiles(actionRoot, "action.yml", SearchOption.AllDirectories))
            {
                yield return file;
            }
        }
    }

    private static bool TryReadAssignment(string line, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var trimmed = line.TrimStart();
        if (trimmed.Length == 0 || trimmed[0] == '#')
        {
            return false;
        }

        var match = AssignmentPattern().Match(trimmed);
        if (!match.Success)
        {
            return false;
        }

        key = match.Groups["key"].Value;
        value = match.Groups["value"].Value.Trim();
        return true;
    }

    private static bool IsCredentialKey(string key)
    {
        if (NonCredentialKeys.Contains(key))
        {
            return false;
        }

        return CredentialKeyFragments.Any(fragment => key.Contains(fragment, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Reduces a raw right-hand side to the literal a deployment would actually use, or
    /// reports that the assignment ships no literal at all (an interpolation, a required
    /// variable, or an empty value).
    /// </summary>
    private static bool TryReadLiteral(string raw, out string literal)
    {
        literal = string.Empty;
        if (raw.Length == 0)
        {
            return false;
        }

        // A GitHub Actions expression is evaluated per run; nothing is shipped.
        if (raw.Contains("${{", StringComparison.Ordinal))
        {
            return false;
        }

        var value = raw;

        // A shell line continuation is punctuation, not part of the value.
        while (value.EndsWith('\\'))
        {
            value = value[..^1].TrimEnd();
        }

        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }
        else
        {
            // Unquoted YAML: an inline comment is not part of the value.
            var comment = value.IndexOf(" #", StringComparison.Ordinal);
            if (comment >= 0)
            {
                value = value[..comment].TrimEnd();
            }
        }

        // `${VAR:-default}` ships `default`; `${VAR}`, `${VAR:?message}` and `$${...}` do not.
        var substitution = SubstitutionPattern().Match(value);
        if (substitution.Success)
        {
            value = substitution.Groups["default"].Value;
        }
        else if (value.Contains('$', StringComparison.Ordinal))
        {
            return false;
        }

        value = value.Trim();
        if (value.Length == 0 || value is "null" or "~" or "|" or ">")
        {
            return false;
        }

        // No literal this repository ships contains whitespace or a quote. Anything that does
        // is a command fragment that happened to start with an inline `KEY=value` assignment
        // (`PGPASSWORD=honua psql ...`) or an expression, not a shipped credential.
        if (value.Any(char.IsWhiteSpace) || value.AsSpan().IndexOfAny('"', '\'', '`') >= 0)
        {
            return false;
        }

        literal = value;
        return true;
    }

    [GeneratedRegex(@"^(?:-\s+)?(?:(?:-e|--env)\s+)?""?(?<key>[A-Za-z_][A-Za-z0-9_]*)""?\s*[:=]\s*(?<value>.*)$")]
    private static partial Regex AssignmentPattern();

    [GeneratedRegex(@"^\$\{(?<name>[A-Za-z_][A-Za-z0-9_]*):-(?<default>[^}]*)\}$")]
    private static partial Regex SubstitutionPattern();
}
