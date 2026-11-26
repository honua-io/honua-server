// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.HealthCheck;

internal enum QualificationFaultKind
{
    None,

    // Readiness and the deep probe stay healthy while every other route answers 500, so only the
    // deployment's own alarms can see the bad release.
    ServeErrors,

    // Liveness and readiness stay healthy while the deep probe and every other route answer 503.
    Wedged
}

// A fault that only qualification gate stacks set, to prove recovery behind a load balancer that
// still sees healthy targets. Customer installs never carry the setting, and without it the fault is inert.
internal sealed record QualificationFault(QualificationFaultKind Kind, string? Token)
{
    internal const string ConfigurationKey = "Qualification:Fault";

    private const string ServeErrorsValue = "ServeErrors";
    private const string WedgedPrefix = "Wedged:";
    private const int MinimumTokenLength = 8;
    private const int MaximumTokenLength = 64;

    internal static QualificationFault None { get; } = new(QualificationFaultKind.None, null);

    public static QualificationFault Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return None;
        }

        var trimmed = value.Trim();
        if (string.Equals(trimmed, ServeErrorsValue, StringComparison.Ordinal))
        {
            return new QualificationFault(QualificationFaultKind.ServeErrors, null);
        }

        if (trimmed.StartsWith(WedgedPrefix, StringComparison.Ordinal) && IsToken(trimmed[WedgedPrefix.Length..]))
        {
            return new QualificationFault(QualificationFaultKind.Wedged, trimmed[WedgedPrefix.Length..]);
        }

        throw new InvalidOperationException(
            $"{ConfigurationKey} must be '{ServeErrorsValue}' or '{WedgedPrefix}<token>', where the token is " +
            $"{MinimumTokenLength} to {MaximumTokenLength} letters, digits or hyphens. Found '{trimmed}'.");
    }

    private static bool IsToken(string token)
        => token.Length is >= MinimumTokenLength and <= MaximumTokenLength
            && token.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');
}
