// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Security;

/// <summary>
/// A request-supplied secret reference parsed with the whole-string <c>provider:identifier</c>
/// grammar. Placeholders, embedded references and free-form text are not references.
/// </summary>
public readonly record struct RequestSecretReference
{
    /// <summary>The provider segment used for environment variables.</summary>
    public const string EnvironmentProvider = "env";

    private const int MaxLength = 512;
    private const int MaxProviderLength = 32;

    private RequestSecretReference(string provider, string identifier)
    {
        Provider = provider;
        Identifier = identifier;
    }

    /// <summary>Lower-cased provider segment (for example <c>env</c>, <c>aws</c>, <c>azure</c>).</summary>
    public string Provider { get; }

    /// <summary>Everything after the first colon, exactly as supplied.</summary>
    public string Identifier { get; }

    /// <summary>Whether the reference names an environment variable.</summary>
    public bool IsEnvironment => string.Equals(Provider, EnvironmentProvider, StringComparison.Ordinal);

    /// <summary>The reference with a normalized provider segment.</summary>
    public string Canonical => $"{Provider}:{Identifier}";

    /// <summary>
    /// Parses <paramref name="value"/> as a whole-string reference. Returns <see langword="false"/>
    /// for blank input, surrounding or embedded whitespace, control characters, placeholder or
    /// connection-string punctuation, an invalid provider segment, or an environment variable
    /// identifier that is not a plain variable name.
    /// </summary>
    public static bool TryParse([NotNullWhen(true)] string? value, out RequestSecretReference reference)
    {
        reference = default;

        if (string.IsNullOrEmpty(value) || value.Length > MaxLength)
        {
            return false;
        }

        var colonIndex = value.IndexOf(':', StringComparison.Ordinal);
        if (colonIndex <= 0 || colonIndex > MaxProviderLength || colonIndex == value.Length - 1)
        {
            return false;
        }

        var provider = value[..colonIndex];
        if (!char.IsAsciiLetter(provider[0]) ||
            !provider.All(static c => char.IsAsciiLetterOrDigit(c) || c == '-'))
        {
            return false;
        }

        var identifier = value[(colonIndex + 1)..];
        if (identifier.Any(static c => char.IsWhiteSpace(c) || char.IsControl(c) || c is '{' or '}' or '$' or ';'))
        {
            return false;
        }

        var normalizedProvider = provider.ToLowerInvariant();
        if (string.Equals(normalizedProvider, EnvironmentProvider, StringComparison.Ordinal) &&
            !RequestSecretReferenceOptions.EnvironmentVariableNamePattern().IsMatch(identifier))
        {
            return false;
        }

        reference = new RequestSecretReference(normalizedProvider, identifier);
        return true;
    }
}
