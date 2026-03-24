// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin;

/// <summary>
/// Background service that polls a configured git repository for manifest changes
/// and either auto-applies them or queues them for approval.
/// </summary>
internal sealed partial class GitOpsWatchService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly GitOpsWatchOptions _options;
    private readonly ILogger<GitOpsWatchService> _logger;

    public GitOpsWatchService(
        IServiceScopeFactory scopeFactory,
        IOptions<GitOpsWatchOptions> options,
        ILogger<GitOpsWatchService> logger)
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Allow the rest of the application to start before beginning polls
        await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);

        while (!stoppingToken.IsCancellationRequested)
        {
            var pollInterval = TimeSpan.FromSeconds(60); // default fallback

            try
            {
                if (!_options.Enabled)
                {
                    await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                pollInterval = await PollAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogPollFailed(_logger, ex);
            }

            await Task.Delay(pollInterval, stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task<TimeSpan> PollAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var watchStore = scope.ServiceProvider.GetRequiredService<IGitOpsWatchStore>();

        var config = await watchStore.GetConfigAsync(cancellationToken).ConfigureAwait(false);
        if (config == null || !config.Enabled)
        {
            return TimeSpan.FromSeconds(60);
        }

        var pollInterval = TimeSpan.FromSeconds(Math.Max(_options.MinPollIntervalSeconds, config.PollIntervalSeconds));

        LogPollStarted(_logger, config.RepositoryUrl, config.Branch);

        var latestCommit = await FetchLatestCommitAsync(config, cancellationToken).ConfigureAwait(false);
        if (latestCommit == null)
        {
            LogPollNoCommit(_logger, config.RepositoryUrl);
            return pollInterval;
        }

        if (string.Equals(latestCommit.Sha, config.LastKnownCommitSha, StringComparison.Ordinal))
        {
            LogPollNoChanges(_logger, config.RepositoryUrl, latestCommit.Sha);
            await watchStore.UpdatePollStateAsync(config.ConfigId, latestCommit.Sha, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            return pollInterval;
        }

        LogPollNewCommit(_logger, config.RepositoryUrl, latestCommit.Sha);

        var fetchResult = await FetchManifestContentAsync(config, latestCommit.Sha, cancellationToken)
            .ConfigureAwait(false);

        if (fetchResult == null)
        {
            LogManifestFetchFailed(_logger, config.RepositoryUrl, config.ManifestPath);
            await watchStore.UpdatePollStateAsync(config.ConfigId, latestCommit.Sha, DateTimeOffset.UtcNow, cancellationToken)
                .ConfigureAwait(false);
            return pollInterval;
        }

        // Populate commit metadata from the fetched commit
        latestCommit = new GitCommitInfo
        {
            Sha = latestCommit.Sha,
            Author = fetchResult.CommitAuthor,
            Message = fetchResult.CommitMessage,
            Timestamp = fetchResult.CommitTimestamp
        };

        // Get previous manifest for diff
        JsonElement? previousManifest = null;
        if (!string.IsNullOrEmpty(config.LastKnownCommitSha))
        {
            var lastChanges = await watchStore.ListChangeRecordsAsync(1, 0, cancellationToken).ConfigureAwait(false);
            if (lastChanges.Count > 0)
            {
                previousManifest = lastChanges[0].ManifestAfter;
            }
        }

        var now = DateTimeOffset.UtcNow;

        var manifest = fetchResult.ManifestContent;

        if (config.ApprovalRequired)
        {
            await HandleApprovalRequiredAsync(scope, watchStore, config, latestCommit, manifest, previousManifest, now, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await HandleAutoApplyAsync(scope, watchStore, config, latestCommit, manifest, previousManifest, now, cancellationToken)
                .ConfigureAwait(false);
        }

        await watchStore.UpdatePollStateAsync(config.ConfigId, latestCommit.Sha, now, cancellationToken)
            .ConfigureAwait(false);

        return pollInterval;
    }

    private async Task HandleApprovalRequiredAsync(
        AsyncServiceScope scope,
        IGitOpsWatchStore watchStore,
        GitOpsWatchConfig config,
        GitCommitInfo commit,
        JsonElement manifestContent,
        JsonElement? previousManifest,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var pendingStore = scope.ServiceProvider.GetRequiredService<IManifestPendingChangeStore>();
        var schemaRegistry = scope.ServiceProvider.GetRequiredService<IMetadataSchemaRegistry>();

        // Parse and validate resources from the manifest
        var resources = DeserializeManifestResources(manifestContent);
        if (resources == null || resources.Length == 0)
        {
            LogManifestEmpty(_logger, config.RepositoryUrl);
            return;
        }

        var (normalizedResources, validationError) = AdminMetadataEndpoints.ValidateAndNormalizeResources(resources!, schemaRegistry);
        if (normalizedResources == null)
        {
            var changeRecord = new GitOpsChangeRecord
            {
                ChangeId = Guid.NewGuid(),
                ConfigId = config.ConfigId,
                CommitSha = commit.Sha,
                CommitMessage = commit.Message,
                CommitAuthor = commit.Author,
                CommitTimestamp = commit.Timestamp,
                ManifestBefore = previousManifest,
                ManifestAfter = manifestContent,
                Status = GitOpsChangeStatus.Failed,
                ErrorMessage = $"Manifest validation failed: {validationError}",
                DetectedAt = now
            };
            await watchStore.CreateChangeRecordAsync(changeRecord, cancellationToken).ConfigureAwait(false);
            LogManifestValidationFailed(_logger, config.RepositoryUrl, validationError ?? "Unknown error");
            return;
        }

        var snapshotJson = JsonSerializer.SerializeToElement(new ManifestApplyRequest
        {
            Resources = normalizedResources,
            DryRun = false,
            Prune = config.PruneEnabled
        }, MetadataResourceJsonContext.Default.ManifestApplyRequest);

        var manifestHash = AdminMetadataEndpoints.ComputeManifestHash(normalizedResources);

        var approvalOptions = scope.ServiceProvider.GetRequiredService<IOptions<ManifestApprovalOptions>>();
        var expiresAt = approvalOptions.Value.DefaultTimeoutMinutes.HasValue
            ? now.AddMinutes(approvalOptions.Value.DefaultTimeoutMinutes.Value)
            : (DateTimeOffset?)null;

        var pending = new ManifestPendingChange
        {
            PendingId = Guid.NewGuid(),
            ManifestSnapshot = snapshotJson,
            ManifestHash = manifestHash,
            Status = ManifestApprovalStatus.Pending,
            RequestedBy = $"gitops:{commit.Author ?? "unknown"}",
            RequestedReason = $"Git commit {commit.Sha[..Math.Min(8, commit.Sha.Length)]}: {commit.Message ?? "(no message)"}",
            DryRun = false,
            Prune = config.PruneEnabled,
            ResourceCount = normalizedResources.Count,
            CreatedAt = now,
            ExpiresAt = expiresAt
        };

        await pendingStore.CreateAsync(pending, cancellationToken).ConfigureAwait(false);

        var changeRecord2 = new GitOpsChangeRecord
        {
            ChangeId = Guid.NewGuid(),
            ConfigId = config.ConfigId,
            CommitSha = commit.Sha,
            CommitMessage = commit.Message,
            CommitAuthor = commit.Author,
            CommitTimestamp = commit.Timestamp,
            ManifestBefore = previousManifest,
            ManifestAfter = manifestContent,
            Status = GitOpsChangeStatus.PendingApproval,
            PendingApprovalId = pending.PendingId,
            DetectedAt = now
        };
        await watchStore.CreateChangeRecordAsync(changeRecord2, cancellationToken).ConfigureAwait(false);

        LogChangeQueuedForApproval(_logger, commit.Sha, pending.PendingId);
    }

    private async Task HandleAutoApplyAsync(
        AsyncServiceScope scope,
        IGitOpsWatchStore watchStore,
        GitOpsWatchConfig config,
        GitCommitInfo commit,
        JsonElement manifestContent,
        JsonElement? previousManifest,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var schemaRegistry = scope.ServiceProvider.GetRequiredService<IMetadataSchemaRegistry>();
        var resourceStore = scope.ServiceProvider.GetRequiredService<IMetadataResourceStore>();
        var compiler = scope.ServiceProvider.GetRequiredService<IMetadataCompiler>();
        var versionStore = scope.ServiceProvider.GetRequiredService<IManifestVersionStore>();

        var resources = DeserializeManifestResources(manifestContent);
        if (resources == null || resources.Length == 0)
        {
            LogManifestEmpty(_logger, config.RepositoryUrl);
            return;
        }

        var (normalizedResources, validationError) = AdminMetadataEndpoints.ValidateAndNormalizeResources(resources!, schemaRegistry);
        if (normalizedResources == null)
        {
            var failedRecord = new GitOpsChangeRecord
            {
                ChangeId = Guid.NewGuid(),
                ConfigId = config.ConfigId,
                CommitSha = commit.Sha,
                CommitMessage = commit.Message,
                CommitAuthor = commit.Author,
                CommitTimestamp = commit.Timestamp,
                ManifestBefore = previousManifest,
                ManifestAfter = manifestContent,
                Status = GitOpsChangeStatus.Failed,
                ErrorMessage = $"Manifest validation failed: {validationError}",
                DetectedAt = now
            };
            await watchStore.CreateChangeRecordAsync(failedRecord, cancellationToken).ConfigureAwait(false);
            LogManifestValidationFailed(_logger, config.RepositoryUrl, validationError ?? "Unknown error");
            return;
        }

        ManifestApplyResult applyResult;
        try
        {
            applyResult = await AdminMetadataEndpoints.ApplyNormalizedResourcesAsync(
                normalizedResources,
                dryRun: false,
                prune: config.PruneEnabled,
                resourceStore,
                compiler,
                cancellationToken,
                versionStore,
                $"gitops:{commit.Author ?? "unknown"}").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            var failedRecord = new GitOpsChangeRecord
            {
                ChangeId = Guid.NewGuid(),
                ConfigId = config.ConfigId,
                CommitSha = commit.Sha,
                CommitMessage = commit.Message,
                CommitAuthor = commit.Author,
                CommitTimestamp = commit.Timestamp,
                ManifestBefore = previousManifest,
                ManifestAfter = manifestContent,
                Status = GitOpsChangeStatus.Failed,
                ErrorMessage = ex.Message,
                DetectedAt = now
            };
            await watchStore.CreateChangeRecordAsync(failedRecord, cancellationToken).ConfigureAwait(false);
            LogApplyFailed(_logger, commit.Sha, ex);
            return;
        }

        var summary = $"Created: {applyResult.Summary.Created}, Updated: {applyResult.Summary.Updated}, " +
                       $"Deleted: {applyResult.Summary.Deleted}, Skipped: {applyResult.Summary.Skipped}";

        var changeRecord = new GitOpsChangeRecord
        {
            ChangeId = Guid.NewGuid(),
            ConfigId = config.ConfigId,
            CommitSha = commit.Sha,
            CommitMessage = commit.Message,
            CommitAuthor = commit.Author,
            CommitTimestamp = commit.Timestamp,
            ManifestBefore = previousManifest,
            ManifestAfter = manifestContent,
            Status = GitOpsChangeStatus.Applied,
            ApplySummary = summary,
            DetectedAt = now,
            AppliedAt = DateTimeOffset.UtcNow
        };
        await watchStore.CreateChangeRecordAsync(changeRecord, cancellationToken).ConfigureAwait(false);

        LogChangeApplied(_logger, commit.Sha, summary);
    }

    /// <summary>
    /// Fetches the latest commit information from the configured git repository
    /// using the git CLI.
    /// </summary>
    private static async Task<GitCommitInfo?> FetchLatestCommitAsync(
        GitOpsWatchConfig config,
        CancellationToken cancellationToken)
    {
        // Use git ls-remote to get the latest commit SHA without cloning.
        // The '--' separator prevents option injection via RepositoryUrl.
        var result = await RunGitCommandAsync(
            ["ls-remote", "--", config.RepositoryUrl, $"refs/heads/{config.Branch}"],
            cancellationToken).ConfigureAwait(false);

        if (result == null || string.IsNullOrWhiteSpace(result.Output))
        {
            return null;
        }

        // Output format: "<sha>\trefs/heads/<branch>"
        var parts = result.Output.Split('\t', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            return null;
        }

        var sha = parts[0].Trim();
        if (!IsValidCommitSha(sha))
        {
            return null;
        }

        return new GitCommitInfo
        {
            Sha = sha,
            // Detailed commit info will be populated during clone/fetch
            Author = null,
            Message = null,
            Timestamp = null
        };
    }

    /// <summary>
    /// Fetches manifest file content and commit metadata from the repository at a specific commit.
    /// Uses sparse checkout to retrieve only manifest files without a full clone.
    /// </summary>
    private static async Task<ManifestFetchResult?> FetchManifestContentAsync(
        GitOpsWatchConfig config,
        string commitSha,
        CancellationToken cancellationToken)
    {
        // Create a temporary directory for the sparse checkout
        var tempDir = Path.Combine(Path.GetTempPath(), $"honua-gitops-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);

            // Initialize, configure sparse checkout, and fetch only manifest files.
            // The '--' separator prevents option injection via RepositoryUrl.
            await RunGitCommandAsync(["init", "--", tempDir], cancellationToken).ConfigureAwait(false);
            await RunGitCommandAsync(
                ["-C", tempDir, "remote", "add", "origin", "--", config.RepositoryUrl],
                cancellationToken).ConfigureAwait(false);
            await RunGitCommandAsync(
                ["-C", tempDir, "config", "core.sparseCheckout", "true"],
                cancellationToken).ConfigureAwait(false);

            // Write the manifest path to sparse-checkout config
            var sparseCheckoutDir = Path.Combine(tempDir, ".git", "info");
            Directory.CreateDirectory(sparseCheckoutDir);
            await File.WriteAllTextAsync(
                Path.Combine(sparseCheckoutDir, "sparse-checkout"),
                config.ManifestPath + "\n",
                cancellationToken).ConfigureAwait(false);

            // Fetch only the specific commit
            var fetchResult = await RunGitCommandAsync(
                ["-C", tempDir, "fetch", "--depth", "1", "origin", commitSha],
                cancellationToken).ConfigureAwait(false);
            if (fetchResult == null || fetchResult.ExitCode != 0)
            {
                return null;
            }

            await RunGitCommandAsync(["-C", tempDir, "checkout", "FETCH_HEAD"], cancellationToken).ConfigureAwait(false);

            // Extract commit metadata from the fetched commit
            string? commitAuthor = null;
            string? commitMessage = null;
            DateTimeOffset? commitTimestamp = null;

            var logResult = await RunGitCommandAsync(
                ["-C", tempDir, "log", "-1", "--format=%an%n%s%n%aI", "FETCH_HEAD"],
                cancellationToken).ConfigureAwait(false);

            if (logResult is { ExitCode: 0 } && !string.IsNullOrWhiteSpace(logResult.Output))
            {
                var lines = logResult.Output.Split('\n', 3);
                if (lines.Length >= 1 && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    commitAuthor = lines[0].Trim();
                }

                if (lines.Length >= 2 && !string.IsNullOrWhiteSpace(lines[1]))
                {
                    commitMessage = lines[1].Trim();
                }

                if (lines.Length >= 3 && DateTimeOffset.TryParse(lines[2].Trim(), out var ts))
                {
                    commitTimestamp = ts;
                }
            }

            // Read manifest files and build a combined JSON array.
            // Verify resolved paths stay within tempDir to prevent path traversal.
            var manifestDir = Path.GetFullPath(Path.Combine(tempDir, config.ManifestPath.TrimEnd('/')));
            if (!manifestDir.StartsWith(tempDir, StringComparison.Ordinal))
            {
                return null;
            }

            if (!Directory.Exists(manifestDir))
            {
                // Try as a single file path
                var singleFile = Path.GetFullPath(Path.Combine(tempDir, config.ManifestPath));
                if (!singleFile.StartsWith(tempDir, StringComparison.Ordinal))
                {
                    return null;
                }

                if (File.Exists(singleFile))
                {
                    var content = await File.ReadAllTextAsync(singleFile, cancellationToken).ConfigureAwait(false);
                    using var doc = JsonDocument.Parse(content);
                    return new ManifestFetchResult
                    {
                        ManifestContent = doc.RootElement.Clone(),
                        CommitAuthor = commitAuthor,
                        CommitMessage = commitMessage,
                        CommitTimestamp = commitTimestamp
                    };
                }

                return null;
            }

            var jsonFiles = Directory.GetFiles(manifestDir, "*.json", SearchOption.AllDirectories);
            if (jsonFiles.Length == 0)
            {
                return null;
            }

            var resources = new List<JsonElement>();
            foreach (var file in jsonFiles.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var content = await File.ReadAllTextAsync(file, cancellationToken).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(content);
                var root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in root.EnumerateArray())
                    {
                        resources.Add(item.Clone());
                    }
                }
                else if (root.ValueKind == JsonValueKind.Object)
                {
                    // Check if it's a manifest wrapper with a "resources" array
                    if (root.TryGetProperty("resources", out var resourcesArray) &&
                        resourcesArray.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in resourcesArray.EnumerateArray())
                        {
                            resources.Add(item.Clone());
                        }
                    }
                    else
                    {
                        resources.Add(root.Clone());
                    }
                }
            }

            return new ManifestFetchResult
            {
                ManifestContent = JsonSerializer.SerializeToElement(resources),
                CommitAuthor = commitAuthor,
                CommitMessage = commitMessage,
                CommitTimestamp = commitTimestamp
            };
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup
            }
        }
    }

    private static MetadataResource[]? DeserializeManifestResources(JsonElement manifestContent)
    {
        try
        {
            if (manifestContent.ValueKind == JsonValueKind.Array)
            {
                return JsonSerializer.Deserialize(
                    manifestContent.GetRawText(),
                    MetadataResourceJsonContext.Default.MetadataResourceArray);
            }

            if (manifestContent.ValueKind == JsonValueKind.Object &&
                manifestContent.TryGetProperty("resources", out var resourcesArray))
            {
                return JsonSerializer.Deserialize(
                    resourcesArray.GetRawText(),
                    MetadataResourceJsonContext.Default.MetadataResourceArray);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<GitProcessResult?> RunGitCommandAsync(
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();

            // Read stdout and stderr concurrently to avoid deadlock when
            // a git command fills one pipe buffer while the other is unread.
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await Task.WhenAll(outputTask, errorTask).ConfigureAwait(false);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

            var output = outputTask.Result;
            var error = errorTask.Result;

            return new GitProcessResult
            {
                ExitCode = process.ExitCode,
                Output = output.Trim(),
                Error = error.Trim()
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Validates that a string is a well-formed git commit SHA (40 hex for SHA-1, 64 for SHA-256).
    /// Prevents argument injection when the SHA is interpolated into git CLI arguments.
    /// </summary>
    private static bool IsValidCommitSha(string sha)
    {
        if (sha.Length != 40 && sha.Length != 64)
        {
            return false;
        }

        foreach (var c in sha)
        {
            if (!Uri.IsHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private sealed class GitCommitInfo
    {
        public string Sha { get; init; } = string.Empty;
        public string? Author { get; init; }
        public string? Message { get; init; }
        public DateTimeOffset? Timestamp { get; init; }
    }

    private sealed class ManifestFetchResult
    {
        public JsonElement ManifestContent { get; init; }
        public string? CommitAuthor { get; init; }
        public string? CommitMessage { get; init; }
        public DateTimeOffset? CommitTimestamp { get; init; }
    }

    private sealed class GitProcessResult
    {
        public int ExitCode { get; init; }
        public string Output { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
    }

    [LoggerMessage(EventId = 9401, Level = LogLevel.Information, Message = "GitOps poll started for {RepositoryUrl} branch {Branch}.")]
    private static partial void LogPollStarted(ILogger logger, string repositoryUrl, string branch);

    [LoggerMessage(EventId = 9402, Level = LogLevel.Debug, Message = "GitOps poll found no changes for {RepositoryUrl} at commit {CommitSha}.")]
    private static partial void LogPollNoChanges(ILogger logger, string repositoryUrl, string commitSha);

    [LoggerMessage(EventId = 9403, Level = LogLevel.Information, Message = "GitOps poll detected new commit {CommitSha} for {RepositoryUrl}.")]
    private static partial void LogPollNewCommit(ILogger logger, string repositoryUrl, string commitSha);

    [LoggerMessage(EventId = 9404, Level = LogLevel.Warning, Message = "GitOps poll failed.")]
    private static partial void LogPollFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 9405, Level = LogLevel.Warning, Message = "GitOps poll could not retrieve latest commit from {RepositoryUrl}.")]
    private static partial void LogPollNoCommit(ILogger logger, string repositoryUrl);

    [LoggerMessage(EventId = 9406, Level = LogLevel.Warning, Message = "GitOps could not fetch manifest from {RepositoryUrl} at path {ManifestPath}.")]
    private static partial void LogManifestFetchFailed(ILogger logger, string repositoryUrl, string manifestPath);

    [LoggerMessage(EventId = 9407, Level = LogLevel.Warning, Message = "GitOps manifest from {RepositoryUrl} contained no resources.")]
    private static partial void LogManifestEmpty(ILogger logger, string repositoryUrl);

    [LoggerMessage(EventId = 9408, Level = LogLevel.Warning, Message = "GitOps manifest validation failed for {RepositoryUrl}: {Error}.")]
    private static partial void LogManifestValidationFailed(ILogger logger, string repositoryUrl, string error);

    [LoggerMessage(EventId = 9409, Level = LogLevel.Information, Message = "GitOps change from commit {CommitSha} queued for approval as {PendingId}.")]
    private static partial void LogChangeQueuedForApproval(ILogger logger, string commitSha, Guid pendingId);

    [LoggerMessage(EventId = 9410, Level = LogLevel.Information, Message = "GitOps change from commit {CommitSha} auto-applied: {Summary}.")]
    private static partial void LogChangeApplied(ILogger logger, string commitSha, string summary);

    [LoggerMessage(EventId = 9411, Level = LogLevel.Error, Message = "GitOps auto-apply failed for commit {CommitSha}.")]
    private static partial void LogApplyFailed(ILogger logger, string commitSha, Exception exception);
}
