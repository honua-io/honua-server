// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;

namespace Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;

internal static class ConnectionStringSecretTemplateExpander
{
    public static async Task<string> ExpandAsync(
        string connectionStringTemplate,
        string directPrefix,
        Func<string, bool> canResolve,
        Func<string, CancellationToken, Task<string?>> resolveSecretAsync,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(connectionStringTemplate))
        {
            return connectionStringTemplate;
        }

        if (connectionStringTemplate.StartsWith(directPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var resolvedValue = await resolveSecretAsync(connectionStringTemplate, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrEmpty(resolvedValue) ? connectionStringTemplate : resolvedValue;
        }

        // Single-pass scan into a StringBuilder so substituted secret bytes are never re-parsed
        // as template placeholders and duplicate placeholders are replaced only at their exact span.
        var builder = new StringBuilder(connectionStringTemplate.Length);
        var index = 0;

        while (index < connectionStringTemplate.Length)
        {
            int refStart;
            if (connectionStringTemplate[index] == '$'
                && index + 1 < connectionStringTemplate.Length
                && connectionStringTemplate[index + 1] == '{')
            {
                refStart = index + 2;
            }
            else if (connectionStringTemplate[index] == '{')
            {
                refStart = index + 1;
            }
            else
            {
                builder.Append(connectionStringTemplate[index]);
                index++;
                continue;
            }

            var end = connectionStringTemplate.IndexOf('}', refStart);
            if (end == -1)
            {
                builder.Append(connectionStringTemplate, index, connectionStringTemplate.Length - index);
                break;
            }

            var secretRef = connectionStringTemplate[refStart..end];
            if (!string.IsNullOrWhiteSpace(secretRef) && canResolve(secretRef))
            {
                var resolvedValue = await resolveSecretAsync(secretRef, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(resolvedValue))
                {
                    builder.Append(resolvedValue);
                }
                else
                {
                    builder.Append(connectionStringTemplate, index, end - index + 1);
                }

                index = end + 1;
                continue;
            }

            builder.Append(connectionStringTemplate, index, refStart - index);
            index = refStart;
        }

        return builder.ToString();
    }
}
