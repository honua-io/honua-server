// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Migration.Services;

internal static class MigrationEvidenceReferences
{
    internal static string[] Sanitize(IEnumerable<string> values)
        => values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => SanitizeEvidenceReference(value.Trim()))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();

    private static string SanitizeEvidenceReference(string value)
    {
        var sensitiveTailIndex = value.IndexOfAny(['?', '#']);
        var withoutQueryOrFragment = sensitiveTailIndex >= 0 ? value[..sensitiveTailIndex] : value;

        if (!Uri.TryCreate(withoutQueryOrFragment, UriKind.Absolute, out var uri) ||
            string.IsNullOrEmpty(uri.UserInfo))
        {
            return withoutQueryOrFragment;
        }

        var builder = new UriBuilder(uri)
        {
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri.AbsoluteUri;
    }
}
