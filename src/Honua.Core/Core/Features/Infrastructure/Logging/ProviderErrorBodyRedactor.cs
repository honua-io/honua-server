// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;

namespace Honua.Core.Features.Infrastructure.Logging;

// Cloud provider error bodies can echo request headers, SAS signatures or tokens back to the caller,
// and they flow into exception messages that end up in logs, so they are scrubbed and bounded first.
public static partial class ProviderErrorBodyRedactor
{
    public const int DefaultMaxLength = 512;

    private const string Ellipsis = "…";

    public static string Redact(string? body, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(body) || maxLength <= 0)
        {
            return string.Empty;
        }

        // Bound the regex input first: a multi-megabyte HTML error page would otherwise hit the match
        // timeout and lose the whole diagnostic. The slack keeps a value cut at the limit redactable.
        var truncated = body.Length > maxLength * 4;
        var bounded = truncated ? body[..(maxLength * 4)] : body;

        string redacted;
        try
        {
            redacted = AuthorizationRegex().Replace(bounded, CredentialPathRedaction.Redacted);
            redacted = JwtRegex().Replace(redacted, CredentialPathRedaction.Redacted);
            redacted = CredentialValueRegex().Replace(redacted, "$1" + CredentialPathRedaction.Redacted);
            redacted = UriUserInfoRegex().Replace(redacted, "$1" + CredentialPathRedaction.Redacted + "@");
        }
        catch (RegexMatchTimeoutException)
        {
            return CredentialPathRedaction.Redacted;
        }

        var sanitized = LogValueRedactor.SanitizeForLog(redacted, maxLength);
        return truncated || redacted.Length > maxLength ? sanitized + Ellipsis : sanitized;
    }

    [GeneratedRegex(
        @"\b(?:Bearer|Basic|SharedKey)\s+[^\s""'<>,]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex AuthorizationRegex();

    [GeneratedRegex(
        @"\b((?:[\w-]*(?:password|passwd|secret|token|api[_-]?key|access[_-]?key|account[_-]?key|credential|signature)[\w-]*|pwd|sig)(?<!exception)[""']?\s*[=:]\s*)(?:""(?:\\.|[^""\\])*(?:""|$)|'(?:\\.|[^'\\])*(?:'|$)|[^\s,;&""'<>}]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex CredentialValueRegex();

    [GeneratedRegex(
        @"\beyJ[\w-]{8,}\.[\w-]+\.[\w-]*",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex JwtRegex();

    [GeneratedRegex(
        @"([a-zA-Z][a-zA-Z0-9+.\-]*://)[^/\s:@]+:[^/\s@]+@",
        RegexOptions.CultureInvariant,
        100)]
    private static partial Regex UriUserInfoRegex();
}
