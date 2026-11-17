// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Configuration;

namespace Honua.ControlPlane;

internal sealed partial class ComposeProjectController
{
    private const string MigrateOnlyFormat = "honua.migrate-only/v1";
    private const int MaxNonceLength = 256;

    internal sealed record ContractGateRefusal(IReadOnlyList<string> Scripts, string? Nonce);

    // A migrate-only summary line; Refusal is set only for a contract-gate refusal that names scripts.
    private readonly record struct MigrateOnlySummary(ContractGateRefusal? Refusal);

    // Converge migrates the candidate in migrate-only mode before starting it. The contract gate
    // refuses before any script is applied, so the stopped prior restarts unchanged and the
    // operator gets the gate's own code instead of an unhealthy candidate start. The run adds only
    // the migrate-only switch, so an approval the install's Compose files forward still applies.
    internal async Task<ComposeExecutorResponse?> PrepareCandidateAsync(string image, CancellationToken cancellationToken)
    {
        var overridePath = OverridePath();
        string? priorOverride;
        try
        {
            priorOverride = File.Exists(overridePath)
                ? await File.ReadAllTextAsync(overridePath, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Returned, not thrown: converge attaches the backup to this response, which keeps recovery.
            return Refuse(null, ComposeExecutorCodes.ReadbackIncomplete, "The prior image override could not be read.");
        }

        var written = await WriteImageOverrideAsync(image, cancellationToken).ConfigureAwait(false);
        if (written != null)
        {
            return written;
        }

        var migrated = await RunMigrateOnlyAsync(cancellationToken).ConfigureAwait(false);
        if (migrated.ExitCode == 0)
        {
            return null;
        }

        var refusal = ContractGateRefusalFrom(migrated.Stdout);
        return refusal == null
            ? Refuse(null, ComposeExecutorCodes.ReadbackIncomplete, "The candidate's database migration did not complete.")
            : await RefuseAtContractGateAsync(overridePath, priorOverride, refusal).ConfigureAwait(false);
    }

    // A named one-shot container, removed unless the run succeeded, so a timed-out or cancelled
    // run cannot keep migrating after recovery restores the backup.
    private async Task<DockerProcessResult> RunMigrateOnlyAsync(CancellationToken cancellationToken)
    {
        var name = _options.ProjectName + "-migrate-" + Guid.NewGuid().ToString("N")[..12];
        var succeeded = false;
        try
        {
            var result = await ComposeAsync(
                TimeSpan.FromSeconds(_options.ComposeWaitTimeoutSeconds + 60),
                ["run", "--rm", "--no-deps", "-T", "--name", name, "-e", "HONUA_MIGRATE_ONLY=true", "--", _options.ServiceName],
                cancellationToken).ConfigureAwait(false);
            succeeded = result.ExitCode == 0;
            return result;
        }
        finally
        {
            if (!succeeded)
            {
                await DockerAsync(CommandTimeout, ["rm", "-f", "--", name], CancellationToken.None).ConfigureAwait(false);
            }
        }
    }

    private async Task<ComposeExecutorResponse> RefuseAtContractGateAsync(
        string overridePath, string? priorOverride, ContractGateRefusal refusal)
    {
        if (!await RestorePriorOverrideAsync(overridePath, priorOverride).ConfigureAwait(false))
        {
            return Refuse(null, ComposeExecutorCodes.ReadbackIncomplete, "The prior image override could not be restored.");
        }

        var started = await ComposeAsync(CommandTimeout, ["start", "--", _options.ServiceName], CancellationToken.None)
            .ConfigureAwait(false);
        if (started.ExitCode != 0)
        {
            return Refuse(null, ComposeExecutorCodes.ReadbackIncomplete, "The prior revision could not be restarted.");
        }

        var approval = refusal.Nonce == null
            ? string.Empty
            : " The approval nonce for exactly these scripts (" + MigrationSafetyOptions.ApproveContractMigrationsKey
                + ") is " + refusal.Nonce + ".";
        return Refuse(
            null,
            ComposeExecutorCodes.ContractApprovalRequired,
            "The release has contract migrations that need operator approval before they are applied: "
            + string.Join(", ", refusal.Scripts)
            + ". Nothing was migrated and the prior revision was restarted. Review the scripts and upgrade with the"
            + " manual migrate-only step in the upgrade and rollback guide." + approval);
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

    // The last migrate-only summary line decides; any outcome other than a contract-gate refusal
    // that names its scripts is a migration failure.
    internal static ContractGateRefusal? ContractGateRefusalFrom(string stdout)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Reverse())
        {
            if (!line.StartsWith('{'))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                if (ReadSummary(document.RootElement) is { } summary)
                {
                    return summary.Refusal;
                }
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException)
            {
                // A log line that merely starts with a brace is not the summary.
            }
        }

        return null;
    }

    private static MigrateOnlySummary? ReadSummary(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("format", out var format) || Text(format) != MigrateOnlyFormat)
        {
            return null;
        }

        if (!root.TryGetProperty("code", out var code) || Text(code) != ComposeExecutorCodes.ContractApprovalRequired
            || !root.TryGetProperty("pendingScripts", out var pending) || pending.ValueKind != JsonValueKind.Array)
        {
            return new MigrateOnlySummary(null);
        }

        var scripts = pending.EnumerateArray()
            .Where(script => script.ValueKind == JsonValueKind.Object
                && script.TryGetProperty("classification", out var kind) && Text(kind) != "expand")
            .Select(script => script.TryGetProperty("script", out var name) ? Text(name) : null)
            .OfType<string>()
            .Where(name => ComposeExecutorRules.ScriptName().IsMatch(name))
            .ToArray();
        return new MigrateOnlySummary(scripts.Length == 0 ? null : new ContractGateRefusal(scripts, ApprovalNonce(root)));
    }

    private static string? Text(JsonElement value)
        => value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static string? ApprovalNonce(JsonElement root)
        => root.TryGetProperty("contractApproval", out var approval)
            && approval.ValueKind == JsonValueKind.Object
            && approval.TryGetProperty("nonce", out var value)
            && Text(value) is { Length: > 0 and <= MaxNonceLength } nonce
            && nonce.All(char.IsAsciiLetterOrDigit)
                ? nonce : null;
}
