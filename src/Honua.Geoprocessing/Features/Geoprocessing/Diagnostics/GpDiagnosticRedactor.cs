// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.RegularExpressions;
using Honua.Core.Features.Geoprocessing.Domain;

namespace Honua.Geoprocessing.Diagnostics;

// Identity fields are not path-scrubbed, because a repository or image reference contains slashes and must survive unless it carries a credential.
internal static partial class GpDiagnosticRedactor
{
    internal const string Redacted = "[REDACTED]";

    internal const string RedactedConnection = "[REDACTED_CONNECTION]";

    internal const int DefaultMaxLength = 4000;

    private const int HardCap = 32_768;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    internal static string RedactProse(string? text, int maxLength = DefaultMaxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var slice = text.Length > HardCap ? text[..HardCap] : text;
        try
        {
            var secrets = ApplySecrets(slice);
            return WorkspacePathSanitizer.SanitizeForClient(secrets, workspace: null, maxLength);
        }
        catch (RegexMatchTimeoutException)
        {
            return Redacted;
        }
    }

    // An identity that contains a credential is dropped whole, because a mangled reference would still keep the secret.
    internal static string? RedactIdentity(string? value, int maxLength = 512)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength)
        {
            trimmed = trimmed[..maxLength];
        }

        try
        {
            var redacted = ApplySecrets(trimmed);
            return string.Equals(redacted, trimmed, StringComparison.Ordinal) ? trimmed : Redacted;
        }
        catch (RegexMatchTimeoutException)
        {
            return Redacted;
        }
    }

    internal static string? RedactDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        try
        {
            if (!string.Equals(ApplySecrets(trimmed), trimmed, StringComparison.Ordinal))
            {
                return null;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }

        var match = Digest().Match(trimmed);
        if (!match.Success)
        {
            return null;
        }

        var prefix = match.Groups["prefix"].Success ? "sha256:" : string.Empty;
        return prefix + match.Groups["hex"].Value.ToLowerInvariant();
    }

    // Non-UTF-8 bytes are left unchanged only when a Latin-1 scan finds no credential, and absolute paths are kept because they can be the dataset.
    internal static (byte[] Bytes, bool RedactionApplied) RedactContent(byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        string text;
        var utf8 = true;
        try
        {
            text = StrictUtf8.GetString(content);
        }
        catch (DecoderFallbackException)
        {
            utf8 = false;
            text = Encoding.Latin1.GetString(content);
        }

        string redacted;
        try
        {
            redacted = ApplySecrets(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return (Encoding.UTF8.GetBytes(Redacted), true);
        }

        if (string.Equals(redacted, text, StringComparison.Ordinal))
        {
            return (content, false);
        }

        // Invalid UTF-8 that contained a credential is replaced by the redacted scan.
        // Valid UTF-8 is re-encoded so the included bytes match what a reader decodes.
        _ = utf8;
        return (Encoding.UTF8.GetBytes(redacted), true);
    }

    private static string ApplySecrets(string value)
    {
        value = DatabaseUri().Replace(value, RedactedConnection);
        value = HttpUserInfo().Replace(value, RedactedConnection);
        value = ConnectionString().Replace(value, RedactedConnection);
        value = AccountKey().Replace(value, Redacted);
        value = Authorization().Replace(value, Redacted);
        value = AwsAccessKey().Replace(value, Redacted);
        return NamedSecret().Replace(value, "$1=[REDACTED]");
    }

    [GeneratedRegex("^(?:(?<prefix>sha256:))?(?<hex>[0-9a-fA-F]{64})$", RegexOptions.CultureInvariant, 100)]
    private static partial Regex Digest();

    [GeneratedRegex(
        @"\b(?:postgres(?:ql)?|mysql|mariadb|mongodb(?:\+srv)?|redis|amqps?)://\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex DatabaseUri();

    [GeneratedRegex(
        @"\bhttps?://[^/\s:@]+:[^/\s@]+@\S+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex HttpUserInfo();

    [GeneratedRegex(
        @"\b(?:(?:Server|Host|Data\s*Source|Addr(?:ess)?|User\s*ID|Uid|Username|Password|Pwd|Database|Initial\s*Catalog)\s*=\s*[^;\r\n]+)(?:\s*;\s*(?:Server|Host|Data\s*Source|Addr(?:ess)?|User\s*ID|Uid|Username|Password|Pwd|Database|Initial\s*Catalog)\s*=\s*[^;\r\n]+)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex ConnectionString();

    [GeneratedRegex(
        @"\b(?:AccountKey|SharedAccessSignature|sig)\s*=\s*[^\s&;]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex AccountKey();

    [GeneratedRegex(
        @"\b(?:Bearer|Basic)\s+[A-Za-z0-9._~+/=-]+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex Authorization();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex AwsAccessKey();

    [GeneratedRegex(
        @"\b(password|passwd|pwd|secret|token|api[-_]?key|access[-_]?key|authorization)\b\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;}&]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        100)]
    private static partial Regex NamedSecret();
}
