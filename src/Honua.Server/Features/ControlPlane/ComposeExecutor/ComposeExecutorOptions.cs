// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

/// <summary>
/// Operator configuration for the Compose proposal executor. Bound from
/// <c>ControlPlane:ComposeExecutor</c> (environment <c>ControlPlane__ComposeExecutor__*</c>).
/// Properties use setters because the configuration binding source generator rejects init-only
/// properties. The serving process reads <see cref="SocketPath"/> and <see cref="ReleaseImage"/>
/// only. The sidecar reads the rest and is the only process with the Docker socket.
/// </summary>
internal sealed class ComposeExecutorOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "ControlPlane:ComposeExecutor";

    /// <summary>Unix socket the serving process uses to reach the sidecar.</summary>
    public string SocketPath { get; set; } = string.Empty;

    /// <summary>Directory that contains the Compose file. The sidecar must be able to write the image override here.</summary>
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>Compose project name. Taken from configuration, never from a model request.</summary>
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>
    /// Compose file relative to <see cref="ProjectDirectory"/>. Used when <see cref="ComposeFiles"/> is empty.
    /// </summary>
    public string ComposeFile { get; set; } = "compose.yaml";

    /// <summary>
    /// Ordered compose files relative to <see cref="ProjectDirectory"/>. Later files add to earlier ones.
    /// The image override is appended after this list. Empty means <see cref="ComposeFile"/> alone.
    /// The model cannot add a file.
    /// </summary>
    public string[] ComposeFiles { get; set; } = [];

    /// <summary>Exact immutable images and schemas for older images that do not carry a schema label.</summary>
    public List<ComposeImageSchemaOption> Revisions { get; set; } = [];

    /// <summary>Service whose image the executor replaces.</summary>
    public string ServiceName { get; set; } = "honua";

    /// <summary>Registered target bound to this single-project executor.</summary>
    public string TargetId { get; set; } = "compose-local";

    /// <summary>Redis service observed and eligible for approved restart.</summary>
    public string RedisService { get; set; } = "redis";

    /// <summary>Serving bootstrap credential used only for the authenticated version readback.</summary>
    public string VersionProbeApiKey { get; set; } = string.Empty;

    /// <summary>Postgres service used for <c>pg_dump</c> and <c>pg_restore</c>.</summary>
    public string PostgresService { get; set; } = "postgres";

    /// <summary>Database name inside the Postgres service.</summary>
    public string DatabaseName { get; set; } = "honua";

    /// <summary>Database user. The local Postgres socket is trust, so the executor does not pass a password.</summary>
    public string DatabaseUser { get; set; } = "honua";

    /// <summary>Directory of backup manifests and <c>audit.jsonl</c>. The operator creates it. The product does not.</summary>
    public string BackupDirectory { get; set; } = string.Empty;

    /// <summary>Operator URL that must return HTTP 200 and a non-empty body after a change.</summary>
    public string CustomerProbeUrl { get; set; } = string.Empty;

    /// <summary>Operator URL that must return HTTP 200 after a change.</summary>
    public string HealthProbeUrl { get; set; } = string.Empty;

    /// <summary>Immutable digest of the release the executor is allowed to converge to.</summary>
    public string ReleaseImage { get; set; } = string.Empty;

    /// <summary>Per-probe HTTP timeout in seconds.</summary>
    public int ProbeTimeoutSeconds { get; set; } = 10;

    /// <summary><c>docker compose up --wait-timeout</c> in seconds.</summary>
    public int ComposeWaitTimeoutSeconds { get; set; } = 180;

    /// <summary>
    /// Validates the fields the sidecar needs before it binds a socket. Returns null when the
    /// configuration can serve. A relative compose file that escapes the project directory is a
    /// scope violation.
    /// </summary>
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

    /// <summary>
    /// Validates the fields the controller needs before any docker invocation. Unit tests call this
    /// path without a socket.
    /// </summary>
    internal ComposeExecutorConfigProblem? ValidateForProcess()
    {
        if (string.IsNullOrWhiteSpace(ProjectDirectory) || !Path.IsPathRooted(ProjectDirectory) || !Directory.Exists(ProjectDirectory))
        {
            return Unconfigured("ControlPlane:ComposeExecutor:ProjectDirectory must be an existing absolute directory.");
        }

        if (!ComposeExecutorRules.ComposeName().IsMatch(ProjectName)
            || !ComposeExecutorRules.ComposeName().IsMatch(ServiceName)
            || !ComposeExecutorRules.ComposeName().IsMatch(PostgresService)
            || !ComposeExecutorRules.ComposeName().IsMatch(RedisService))
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

    /// <summary>Absolute paths of the configured compose files, in operator order.</summary>
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

/// <summary>A configuration problem that refuses the executor before docker runs.</summary>
internal readonly record struct ComposeExecutorConfigProblem(string Code, string Message);

/// <summary>Operator-owned schema catalog entry for an immutable image without a schema label.</summary>
internal sealed class ComposeImageSchemaOption
{
    /// <summary>Exact immutable image reference or image id.</summary>
    public string Image { get; set; } = string.Empty;

    /// <summary>Highest numbered migration shipped by that image.</summary>
    public int MaxSchema { get; set; }
}
