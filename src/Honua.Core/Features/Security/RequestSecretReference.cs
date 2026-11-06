// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;

namespace Honua.Core.Features.Security;

// Only a whole-string provider:identifier is a reference; a placeholder, an embedded reference, or free-form text is not.
public readonly record struct RequestSecretReference
{
    public const string EnvironmentProvider = "env";

    private const int MaxLength = 512;
    private const int MaxProviderLength = 32;

    private RequestSecretReference(string provider, string identifier)
    {
        Provider = provider;
        Identifier = identifier;
    }

    public string Provider { get; }

    public string Identifier { get; }

    public bool IsEnvironment => string.Equals(Provider, EnvironmentProvider, StringComparison.Ordinal);

    public string Canonical => $"{Provider}:{Identifier}";

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
