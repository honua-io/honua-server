// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using System.Text.RegularExpressions;

namespace Honua.Server.Features.Identity.Scim;

internal static partial class ScimEndpoints
{
    // RFC 7644 section 3.10 allows an attribute qualified by the core schema URN of its resource.
    private static readonly Dictionary<string, string> QualifiedFilterAttributes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["userName"] = "urn:ietf:params:scim:schemas:core:2.0:User:userName",
        ["displayName"] = "urn:ietf:params:scim:schemas:core:2.0:Group:displayName",
    };

    // RFC 7644 section 3.4.2.2: a filter the server cannot evaluate is refused with invalidFilter, never
    // ignored. An identity provider that probes for an existing account reads an unfiltered list as a match
    // and would then update or deactivate the wrong user.
    private static ScimEqFilter ParseEqFilter(string? filter, string attribute)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return ScimEqFilter.None;
        }

        Match match;
        try
        {
            match = EqFilterPattern().Match(filter);
        }
        catch (RegexMatchTimeoutException)
        {
            return ScimEqFilter.Invalid;
        }

        if (!match.Success || !NamesAttribute(match.Groups["attr"].Value, attribute))
        {
            return ScimEqFilter.Invalid;
        }

        // An empty value would reach the store as "no filter" and list every resource.
        var value = match.Groups["quoted"].Success ? UnescapeFilterValue(match.Groups["quoted"].Value) : match.Groups["bare"].Value;
        return value.Length > 0 ? new ScimEqFilter { IsValid = true, Value = value } : ScimEqFilter.Invalid;
    }

    private static bool NamesAttribute(string candidate, string attribute)
        => string.Equals(candidate, attribute, StringComparison.OrdinalIgnoreCase)
            || (QualifiedFilterAttributes.TryGetValue(attribute, out var qualified)
                && string.Equals(candidate, qualified, StringComparison.OrdinalIgnoreCase));

    private static IResult InvalidFilterResult(string attribute)
        => ScimErrorResult(
            StatusCodes.Status400BadRequest,
            $"Unsupported filter; only '{attribute} eq \"value\"' is supported on this resource.",
            "invalidFilter");

    private static string UnescapeFilterValue(string quoted)
    {
        if (!quoted.Contains('\\', StringComparison.Ordinal))
        {
            return quoted;
        }

        // The pattern only admits \" and \\, so the character after a backslash is always present.
        var builder = new StringBuilder(quoted.Length);
        var index = 0;
        while (index < quoted.Length)
        {
            var skip = quoted[index] == '\\' ? 1 : 0;
            builder.Append(quoted[index + skip]);
            index += skip + 1;
        }

        return builder.ToString();
    }

    [GeneratedRegex(
        """^\s*(?<attr>[A-Za-z][A-Za-z0-9_.:-]*)\s+eq\s+(?:"(?<quoted>(?:[^"\\]|\\["\\])*)"|(?<bare>[^\s"]+))\s*$""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 100)]
    private static partial Regex EqFilterPattern();

    // None: no filter was sent. Invalid: a filter was sent that this endpoint cannot evaluate.
    private readonly record struct ScimEqFilter
    {
        public static ScimEqFilter None => new() { IsValid = true };

        public static ScimEqFilter Invalid => default;

        public bool IsValid { get; init; }

        public string? Value { get; init; }
    }
}
