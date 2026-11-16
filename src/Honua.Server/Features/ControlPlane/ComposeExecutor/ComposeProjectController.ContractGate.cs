// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;

namespace Honua.ControlPlane;

internal sealed partial class ComposeProjectController
{
    private const string MigrateOnlyFormat = "honua.migrate-only/v1";

    // Converge migrates the candidate in migrate-only mode before starting it. The contract gate
    // refuses before any script is applied, so the stopped prior restarts unchanged and the
    // operator gets the gate's own code instead of an unhealthy candidate start (#8097).
    internal async Task<ComposeExecutorResponse?> PrepareCandidateAsync(string image, CancellationToken cancellationToken)
    {
        var overridePath = OverridePath();
        var priorOverride = File.Exists(overridePath)
            ? await File.ReadAllTextAsync(overridePath, cancellationToken).ConfigureAwait(false)
            : null;
        var written = await WriteImageOverrideAsync(image, cancellationToken).ConfigureAwait(false);
        if (written != null)
        {
            return written;
        }

        var migrated = await ComposeAsync(
            TimeSpan.FromSeconds(_options.ComposeWaitTimeoutSeconds + 60),
            ["run", "--rm", "--no-deps", "-T", "-e", "HONUA_MIGRATE_ONLY=true",
                "-e", MigrationSafetyOptions.ApproveContractMigrationsKey + "=", "--", _options.ServiceName],
            cancellationToken).ConfigureAwait(false);
        if (migrated.ExitCode == 0)
        {
            return null;
        }

        var contracts = ContractScriptsAwaitingApproval(migrated.Stdout);
        if (contracts == null)
        {
            return Refuse(null, ComposeExecutorCodes.ReadbackIncomplete, "The candidate's database migration did not complete.");
        }

        if (!await RestorePriorOverrideAsync(overridePath, priorOverride).ConfigureAwait(false))
        {
            return Refuse(null, ComposeExecutorCodes.ReadbackIncomplete, "The prior image override could not be restored.");
        }

        await ComposeAsync(CommandTimeout, ["start", "--", _options.ServiceName], CancellationToken.None).ConfigureAwait(false);
        return Refuse(
            null,
            ComposeExecutorCodes.ContractApprovalRequired,
            "The release has contract migrations that need operator approval before they are applied: "
            + string.Join(", ", contracts)
            + ". Nothing was migrated and the prior revision was restarted.");
    }

    private string OverridePath()
        => Path.Join(Path.GetFullPath(_options.ProjectDirectory), ComposeExecutorProtocol.OverrideFileName);

    private static async Task<bool> RestorePriorOverrideAsync(string overridePath, string? priorOverride)
    {
        try
        {
            if (priorOverride == null)
            {
                File.Delete(overridePath);
                return true;
            }

            var temporary = overridePath + ".tmp";
            await File.WriteAllTextAsync(temporary, priorOverride, CancellationToken.None).ConfigureAwait(false);
            File.Move(temporary, overridePath, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // The last migrate-only summary line names the pending scripts; anything else is a migration failure.
    internal static IReadOnlyList<string>? ContractScriptsAwaitingApproval(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var summary = JsonDocument.Parse(line);
                var root = summary.RootElement;
                if (root.ValueKind != JsonValueKind.Object
                    || !root.TryGetProperty("format", out var format) || format.GetString() != MigrateOnlyFormat)
                {
                    continue;
                }

                if (!root.TryGetProperty("code", out var code) || code.GetString() != ComposeExecutorCodes.ContractApprovalRequired
                    || !root.TryGetProperty("pendingScripts", out var pending) || pending.ValueKind != JsonValueKind.Array)
                {
                    return null;
                }

                return pending.EnumerateArray()
                    .Where(script => script.TryGetProperty("classification", out var kind) && kind.GetString() != "expand")
                    .Select(script => script.TryGetProperty("script", out var name) ? name.GetString() : null)
                    .OfType<string>()
                    .Where(name => ComposeExecutorRules.ScriptName().IsMatch(name))
                    .ToArray();
            }
            catch (JsonException)
            {
                // A log line that merely starts with a brace is not the summary.
            }
        }

        return null;
    }
}
