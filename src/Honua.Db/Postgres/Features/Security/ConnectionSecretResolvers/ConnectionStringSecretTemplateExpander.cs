// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;

internal sealed class SecretTemplateExpansionRequest
{
    public required string ConnectionStringTemplate { get; init; }
    public required string DirectPrefix { get; init; }
    public required Func<string, bool> CanResolve { get; init; }
    public required Func<string, CancellationToken, Task<string?>> ResolveSecretAsync { get; init; }
}

internal static class ConnectionStringSecretTemplateExpander
{
    public static async Task<string> ExpandAsync(
        SecretTemplateExpansionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var template = request.ConnectionStringTemplate;
        if (string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        if (template.StartsWith(request.DirectPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedValue = await request.ResolveSecretAsync(template, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(resolvedValue) ? template : resolvedValue;
        }

        return await ExpandPlaceholdersAsync(request, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<string> ExpandPlaceholdersAsync(
        SecretTemplateExpansionRequest request,
        CancellationToken cancellationToken)
    {
        var template = request.ConnectionStringTemplate;
        var builder = new StringBuilder(template.Length);
        var index = 0;
        while (index < template.Length)
        {
            var refStart = FindPlaceholderContentStart(template, index);
            if (refStart < 0)
            {
                builder.Append(template[index++]);
                continue;
            }

            var end = template.IndexOf('}', refStart);
            if (end < 0)
            {
                builder.Append(template, index, template.Length - index);
                break;
            }

            var secretRef = template[refStart..end];
            if (!string.IsNullOrWhiteSpace(secretRef) && request.CanResolve(secretRef))
            {
                var resolvedValue = await request.ResolveSecretAsync(secretRef, cancellationToken).ConfigureAwait(false);
                AppendExpandedSpan(builder, template[index..(end + 1)], resolvedValue);
                index = end + 1;
                continue;
            }

            builder.Append(template, index, refStart - index);
            index = refStart;
        }

        return builder.ToString();
    }

    private static int FindPlaceholderContentStart(string template, int index)
    {
        if (template[index] == '$' && index + 1 < template.Length && template[index + 1] == '{')
        {
            return index + 2;
        }

        return template[index] == '{' ? index + 1 : -1;
    }

    private static void AppendExpandedSpan(StringBuilder builder, string originalSpan, string? resolvedValue)
        => builder.Append(string.IsNullOrEmpty(resolvedValue) ? originalSpan : resolvedValue);
}
