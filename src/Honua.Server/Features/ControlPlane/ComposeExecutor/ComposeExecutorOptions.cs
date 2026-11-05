// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

// The serving process reads the socket settings only; the sidecar is the only process with the Docker socket.
internal sealed class ComposeExecutorOptions
{
    public const string SectionName = "ControlPlane:ComposeExecutor";

    public string SocketPath { get; set; } = string.Empty;

    // The sidecar must be able to write the image override into the Compose directory.
    public string ProjectDirectory { get; set; } = string.Empty;

    // The Compose project name comes from configuration and never from a model request.
    public string ProjectName { get; set; } = string.Empty;

    public string ComposeFile { get; set; } = "compose.yaml";

    // The model cannot add a compose file; the image override is appended after the configured list.
    public string[] ComposeFiles { get; set; } = [];

    public List<ComposeImageSchemaOption> Revisions { get; set; } = [];

    public string ServiceName { get; set; } = "honua";

    public string TargetId { get; set; } = "compose-local";

    public string RedisService { get; set; } = "redis";

    // This credential is used only for the authenticated version readback.
    public string VersionProbeApiKey { get; set; } = string.Empty;

    public string PostgresService { get; set; } = "postgres";

    public string DatabaseName { get; set; } = "honua";

    public string DatabaseUser { get; set; } = "honua";

    public string BackupDirectory { get; set; } = string.Empty;

    // This URL must return HTTP 200 and a non-empty body after a change.
    public string CustomerProbeUrl { get; set; } = string.Empty;

    // This URL must return HTTP 200 after a change.
    public string HealthProbeUrl { get; set; } = string.Empty;

    public string ReleaseImage { get; set; } = string.Empty;

    public int ProbeTimeoutSeconds { get; set; } = 10;

    public int ComposeWaitTimeoutSeconds { get; set; } = 180;

    internal ComposeExecutorConfigProblem? ValidateForCommand()
    {
        if (string.IsNullOrWhiteSpace(SocketPath) || !Path.IsPathRooted(SocketPath))
        {
            return new ComposeExecutorConfigProblem(
                ComposeExecutorCodes.ExecutorUnconfigured,
                "ControlPlane:ComposeExecutor:SocketPath must be an absolute unix socket path.");
        }

        return ValidateForProcess();
    }

    internal ComposeExecutorConfigProblem? ValidateForProcess()
    {
        if (string.IsNullOrWhiteSpace(ProjectDirectory) || !Path.IsPathRooted(ProjectDirectory) || !Directory.Exists(ProjectDirectory))
        {
            return Unconfigured("ControlPlane:ComposeExecutor:ProjectDirectory must be an existing absolute directory.");
        }

        if (!ComposeExecutorRules.ComposeName().IsMatch(ProjectName)
            || !ComposeExecutorRules.ComposeName().IsMatch(ServiceName)
            || !ComposeExecutorRules.ComposeName().IsMatch(PostgresService)
            || (!string.IsNullOrWhiteSpace(RedisService) && !ComposeExecutorRules.ComposeName().IsMatch(RedisService)))
        {
            return Scope("Compose project and service names are limited to the configured allowlist.");
        }

        if (!ComposeExecutorRules.SqlIdentifier().IsMatch(DatabaseName)
            || !ComposeExecutorRules.SqlIdentifier().IsMatch(DatabaseUser))
        {
            return Scope("Database name and user are limited to SQL identifiers from configuration.");
        }

        if (Revisions.Any(revision => !ComposeExecutorRules.IsImmutableImage(revision.Image) || revision.MaxSchema < 0)
            || Revisions.GroupBy(revision => revision.Image, StringComparer.Ordinal).Any(group => group.Count() > 1))
        {
            return Scope("Revision schemas must name distinct immutable images and non-negative schema numbers.");
        }

        foreach (var composeFile in EffectiveComposeFiles())
        {
            if (string.IsNullOrWhiteSpace(composeFile)
                || Path.IsPathRooted(composeFile)
                || composeFile.Contains("..", StringComparison.Ordinal))
            {
                return Scope("The compose file must be a relative path inside the project directory.");
            }

            var root = Path.GetFullPath(ProjectDirectory);
            var composePath = Path.GetFullPath(Path.Combine(root, composeFile));
            var prefix = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!composePath.StartsWith(prefix, StringComparison.Ordinal))
            {
                return Scope("The compose file must stay inside the project directory.");
            }

            if (!File.Exists(composePath))
            {
                return Unconfigured("The configured compose file does not exist.");
            }
        }

        if (!IsHttpUrl(HealthProbeUrl) || !IsHttpUrl(CustomerProbeUrl))
        {
            return Unconfigured("Health and customer probe URLs must be absolute http or https URLs.");
        }

        if (!ComposeExecutorRules.IsImmutableImage(ReleaseImage))
        {
            return new ComposeExecutorConfigProblem(
                ComposeExecutorCodes.ImageNotImmutable,
                "ControlPlane:ComposeExecutor:ReleaseImage must be an immutable digest.");
        }

        if (string.IsNullOrWhiteSpace(BackupDirectory) || !Path.IsPathRooted(BackupDirectory) || !Directory.Exists(BackupDirectory))
        {
            return Unconfigured("ControlPlane:ComposeExecutor:BackupDirectory must be an existing absolute directory.");
        }

        if (ProbeTimeoutSeconds is < 1 or > 120 || ComposeWaitTimeoutSeconds is < 1 or > 3600)
        {
            return Unconfigured("Probe and compose wait timeouts are outside the allowed range.");
        }

        return null;
    }

    internal string[] ComposeFilePaths()
    {
        var root = Path.GetFullPath(ProjectDirectory);
        var paths = new List<string>(EffectiveComposeFiles().Length);
        foreach (var composeFile in EffectiveComposeFiles())
        {
            if (Path.IsPathRooted(composeFile))
            {
                throw new InvalidOperationException("A compose file path must stay relative.");
            }

            paths.Add(Path.GetFullPath(Path.Combine(root, composeFile)));
        }

        return paths.ToArray();
    }

    private string[] EffectiveComposeFiles()
        => ComposeFiles is { Length: > 0 } ? ComposeFiles : [ComposeFile];

    private static bool IsHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

    private static ComposeExecutorConfigProblem Unconfigured(string message)
        => new(ComposeExecutorCodes.ExecutorUnconfigured, message);

    private static ComposeExecutorConfigProblem Scope(string message)
        => new(ComposeExecutorCodes.ScopeViolation, message);
}

internal readonly record struct ComposeExecutorConfigProblem(string Code, string Message);

internal sealed class ComposeImageSchemaOption
{
    public string Image { get; set; } = string.Empty;

    public int MaxSchema { get; set; }
}
