// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel;

namespace Honua.Core.Features.Infrastructure.Subprocesses;

public static class SubprocessEnvironmentScrubber
{
    private static readonly HashSet<string> InheritedSecretVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "AWS_ACCESS_KEY_ID",
        "AWS_SECRET_ACCESS_KEY",
        "AWS_SESSION_TOKEN",
        "AWS_PROFILE",
        "AWS_WEB_IDENTITY_TOKEN_FILE",
        "AWS_ROLE_ARN",
        "AWS_CONTAINER_CREDENTIALS_FULL_URI",
        "AWS_CONTAINER_CREDENTIALS_RELATIVE_URI",
        "AZURE_STORAGE_CONNECTION_STRING",
        "AZURE_STORAGE_ACCOUNT",
        "AZURE_STORAGE_ACCESS_KEY",
        "AZURE_STORAGE_SAS_TOKEN",
        "AZURE_CLIENT_ID",
        "AZURE_CLIENT_SECRET",
        "AZURE_TENANT_ID",
        "GOOGLE_APPLICATION_CREDENTIALS",
        "PGPASSWORD",
        "PGPASSFILE",
        "PGUSER",
        "PGHOST",
        "PGDATABASE"
    };

    private static readonly string[] SensitiveVariablePrefixes =
    [
        "ConnectionStrings__",
        "Database__",
        "Postgres__",
        "Honua__",
        "HONUA_",
        "Security__",
        "License__",
        "FileStorage__",
        "OTEL_"
    ];

    private static readonly string[] SensitiveVariableSubstrings =
    [
        "SECRET",
        "PASSWORD",
        "PASSWD",
        "TOKEN",
        "API_KEY",
        "APIKEY",
        "PRIVATE_KEY",
        "CONNECTION_STRING",
        "CONNECTIONSTRING",
        "CREDENTIAL",
        "LICENSE_KEY",
        "ENCRYPTION_KEY",
        "SIGNING_KEY"
    ];

    public static bool IsSensitiveVariable(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (InheritedSecretVariables.Contains(name))
        {
            return true;
        }

        foreach (var prefix in SensitiveVariablePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var token in SensitiveVariableSubstrings)
        {
            if (name.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void Scrub(
        IDictionary<string, string?> environment,
        IReadOnlySet<string>? additionalSensitiveNames = null)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var inheritedName in environment.Keys
                     .Where(name => IsSensitiveVariable(name)
                         || (additionalSensitiveNames is not null && additionalSensitiveNames.Contains(name)))
                     .ToArray())
        {
            environment.Remove(inheritedName);
        }
    }

    public static void TryKill(System.Diagnostics.Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited between the check and the kill.
        }
        catch (Win32Exception)
        {
            // Permission/race killing the process; nothing more we can do.
        }
    }
}
