// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Core.Features.Metadata.Schema;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Admin;

public sealed class GitOpsWatchServiceTests : IDisposable
{
    private const string ValidGroupManifest = """
        {
          "apiVersion": "honua.io/v1alpha1",
          "kind": "Group",
          "metadata": {
            "name": "parks"
          },
          "spec": {}
        }
        """;

    private readonly List<string> _tempDirectories = [];

    [UnitTest]
    public async Task PollOnceAsync_MissingManifestPath_DoesNotMarkCommitObserved()
    {
        var repoDir = await CreateLocalRepositoryAsync();
        var latestCommit = await RunGitForOutputAsync(repoDir, "rev-parse", "HEAD");
        var watchStore = new TestGitOpsWatchStore(CreateConfig(repoDir, manifestPath: "missing/"));

        using var services = CreateServices(watchStore);
        var service = CreateService(services);

        await service.PollOnceAsync(CancellationToken.None);

        watchStore.PollStateUpdateCount.Should().Be(0);
        watchStore.CurrentConfig.LastKnownCommitSha.Should().BeNull();
        watchStore.ChangeRecords.Should().BeEmpty();
        latestCommit.Should().HaveLength(40);
    }

    [UnitTest]
    public async Task PollOnceAsync_ExactManifestFile_QueuesApprovalAndMarksCommitObserved()
    {
        var repoDir = await CreateLocalRepositoryAsync(("manifests/group.json", ValidGroupManifest));
        var latestCommit = await RunGitForOutputAsync(repoDir, "rev-parse", "HEAD");
        var watchStore = new TestGitOpsWatchStore(CreateConfig(repoDir, manifestPath: "manifests/group.json"));
        var pendingStore = new TestManifestPendingChangeStore();

        using var services = CreateServices(watchStore, pendingStore);
        var service = CreateService(services);

        await service.PollOnceAsync(CancellationToken.None);

        watchStore.PollStateUpdateCount.Should().Be(1);
        watchStore.CurrentConfig.LastKnownCommitSha.Should().Be(latestCommit);
        watchStore.ChangeRecords.Should().ContainSingle();
        watchStore.ChangeRecords[0].CommitSha.Should().Be(latestCommit);
        watchStore.ChangeRecords[0].Status.Should().Be(GitOpsChangeStatus.PendingApproval);
        pendingStore.Changes.Should().ContainSingle();
        pendingStore.Changes[0].PendingId.Should().Be(watchStore.ChangeRecords[0].PendingApprovalId!.Value);
        pendingStore.Changes[0].ResourceCount.Should().Be(1);
    }

    [UnitTest]
    public async Task PollOnceAsync_DirectoryManifestPath_UsesHonuaManifest()
    {
        var repoDir = await CreateLocalRepositoryAsync(("manifests/honua-manifest.json", ValidGroupManifest));
        var latestCommit = await RunGitForOutputAsync(repoDir, "rev-parse", "HEAD");
        var watchStore = new TestGitOpsWatchStore(CreateConfig(repoDir, manifestPath: "manifests/"));
        var pendingStore = new TestManifestPendingChangeStore();

        using var services = CreateServices(watchStore, pendingStore);
        var service = CreateService(services);

        await service.PollOnceAsync(CancellationToken.None);

        watchStore.PollStateUpdateCount.Should().Be(1);
        watchStore.CurrentConfig.LastKnownCommitSha.Should().Be(latestCommit);
        watchStore.ChangeRecords.Should().ContainSingle();
        watchStore.ChangeRecords[0].Status.Should().Be(GitOpsChangeStatus.PendingApproval);
        pendingStore.Changes.Should().ContainSingle();
        pendingStore.Changes[0].ResourceCount.Should().Be(1);
    }

    [UnitTest]
    public async Task PollOnceAsync_DirectoryManifestPath_UsesManifestFallback()
    {
        var repoDir = await CreateLocalRepositoryAsync(("deploy/manifest.json", ValidGroupManifest));
        var latestCommit = await RunGitForOutputAsync(repoDir, "rev-parse", "HEAD");
        var watchStore = new TestGitOpsWatchStore(CreateConfig(repoDir, manifestPath: "deploy/"));
        var pendingStore = new TestManifestPendingChangeStore();

        using var services = CreateServices(watchStore, pendingStore);
        var service = CreateService(services);

        await service.PollOnceAsync(CancellationToken.None);

        watchStore.PollStateUpdateCount.Should().Be(1);
        watchStore.CurrentConfig.LastKnownCommitSha.Should().Be(latestCommit);
        watchStore.ChangeRecords.Should().ContainSingle();
        watchStore.ChangeRecords[0].Status.Should().Be(GitOpsChangeStatus.PendingApproval);
        pendingStore.Changes.Should().ContainSingle();
        pendingStore.Changes[0].ResourceCount.Should().Be(1);
    }

    [UnitTest]
    public void BuildSparseCheckoutPatterns_SlashlessDirectoryPath_FetchesOnlyDefaultManifestFiles()
    {
        var normalized = GitOpsWatchManifestPath.TryNormalize(
            "manifests",
            out var manifestPath,
            out var errorMessage);

        normalized.Should().BeTrue();
        errorMessage.Should().BeNull();
        manifestPath.Should().Be("manifests/");

        var patterns = GitOpsWatchManifestPath.BuildSparseCheckoutPatterns(manifestPath);

        patterns.Should().Equal(
            "manifests/honua-manifest.json",
            "manifests/manifest.json");
        patterns.Should().NotContain("manifests");
    }

    [UnitTest]
    public async Task PollOnceAsync_DirectoryWithoutDefaultManifest_DoesNotMarkCommitObserved()
    {
        var repoDir = await CreateLocalRepositoryAsync(("manifests/group.json", ValidGroupManifest));
        var latestCommit = await RunGitForOutputAsync(repoDir, "rev-parse", "HEAD");
        var watchStore = new TestGitOpsWatchStore(CreateConfig(repoDir, manifestPath: "manifests/"));

        using var services = CreateServices(watchStore);
        var service = CreateService(services);

        await service.PollOnceAsync(CancellationToken.None);

        watchStore.PollStateUpdateCount.Should().Be(0);
        watchStore.CurrentConfig.LastKnownCommitSha.Should().BeNull();
        watchStore.ChangeRecords.Should().BeEmpty();
        latestCommit.Should().HaveLength(40);
    }

    [UnitTest]
    public async Task PollOnceAsync_GlobManifestPath_RecordsSafeFailureAndDoesNotMarkCommitObserved()
    {
        var repoDir = await CreateLocalRepositoryAsync(("manifests/honua-manifest.json", ValidGroupManifest));
        var latestCommit = await RunGitForOutputAsync(repoDir, "rev-parse", "HEAD");
        var watchStore = new TestGitOpsWatchStore(CreateConfig(repoDir, manifestPath: "manifests/*.json"));

        using var services = CreateServices(watchStore);
        var service = CreateService(services);

        await service.PollOnceAsync(CancellationToken.None);

        watchStore.PollStateUpdateCount.Should().Be(0);
        watchStore.CurrentConfig.LastKnownCommitSha.Should().BeNull();
        watchStore.ChangeRecords.Should().ContainSingle();
        watchStore.ChangeRecords[0].CommitSha.Should().Be(latestCommit);
        watchStore.ChangeRecords[0].Status.Should().Be(GitOpsChangeStatus.Failed);
        watchStore.ChangeRecords[0].ErrorMessage.Should().Be(GitOpsWatchManifestPath.GlobUnsupportedErrorMessage);
        watchStore.ChangeRecords[0].ErrorMessage.Should().NotContain(repoDir);
    }

    [UnitTest]
    public async Task PollOnceAsync_InvalidResourcesManifest_RecordsSafeFailureAndDoesNotMarkCommitObserved()
    {
        const string invalidManifest = """
            {
              "resources": {
                "kind": "Group"
              }
            }
            """;
        var repoDir = await CreateLocalRepositoryAsync(("manifests/honua-manifest.json", invalidManifest));
        var latestCommit = await RunGitForOutputAsync(repoDir, "rev-parse", "HEAD");
        var watchStore = new TestGitOpsWatchStore(CreateConfig(repoDir, manifestPath: "manifests/"));

        using var services = CreateServices(watchStore);
        var service = CreateService(services);

        await service.PollOnceAsync(CancellationToken.None);
        await service.PollOnceAsync(CancellationToken.None);

        watchStore.PollStateUpdateCount.Should().Be(0);
        watchStore.CurrentConfig.LastKnownCommitSha.Should().BeNull();
        watchStore.ChangeRecords.Should().ContainSingle();
        watchStore.ChangeRecords[0].CommitSha.Should().Be(latestCommit);
        watchStore.ChangeRecords[0].Status.Should().Be(GitOpsChangeStatus.Failed);
        watchStore.ChangeRecords[0].ErrorMessage.Should().StartWith("Manifest parse failed:");
        watchStore.ChangeRecords[0].ErrorMessage.Should().NotContain(repoDir);
        watchStore.ChangeRecords[0].ManifestAfter.GetProperty("resources").ValueKind.Should().Be(JsonValueKind.Object);
    }

    public void Dispose()
    {
        foreach (var directory in _tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup of temporary local git repositories.
            }
        }
    }

    private static GitOpsWatchService CreateService(IServiceProvider services) => new(
        services.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new GitOpsWatchOptions
        {
            Enabled = true,
            MinPollIntervalSeconds = 1
        }),
        NullLogger<GitOpsWatchService>.Instance);

    private static ServiceProvider CreateServices(
        TestGitOpsWatchStore watchStore,
        TestManifestPendingChangeStore? pendingStore = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IGitOpsWatchStore>(watchStore);
        services.AddSingleton<IMetadataSchemaRegistry, MetadataSchemaRegistry>();
        services.AddSingleton<IOptions<ManifestApprovalOptions>>(Options.Create(new ManifestApprovalOptions
        {
            Enabled = true,
            DefaultTimeoutMinutes = 60
        }));

        if (pendingStore != null)
        {
            services.AddSingleton<IManifestPendingChangeStore>(pendingStore);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateScopes = true,
            ValidateOnBuild = true
        });
    }

    private static GitOpsWatchConfig CreateConfig(string repositoryUrl, string manifestPath) => new()
    {
        ConfigId = Guid.NewGuid(),
        RepositoryUrl = repositoryUrl,
        Branch = "main",
        ManifestPath = manifestPath,
        PollIntervalSeconds = 30,
        ApprovalRequired = true,
        Enabled = true,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private async Task<string> CreateLocalRepositoryAsync(params (string RelativePath, string Content)[] files)
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"honua-gitops-watch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(repoDir);
        _tempDirectories.Add(repoDir);

        await RunGitCheckedAsync(repoDir, "init");
        await RunGitCheckedAsync(repoDir, "config", "user.email", "gitops@example.test");
        await RunGitCheckedAsync(repoDir, "config", "user.name", "GitOps Test");
        await RunGitCheckedAsync(repoDir, "checkout", "-b", "main");

        if (files.Length > 0)
        {
            foreach (var (relativePath, content) in files)
            {
                var filePath = Path.Combine(repoDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
                await File.WriteAllTextAsync(filePath, content);
            }
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "No manifests in this commit.\n");
        }

        await RunGitCheckedAsync(repoDir, "add", ".");
        await RunGitCheckedAsync(repoDir, "commit", "-m", files.Length > 0 ? "add manifest" : "initial commit");

        return repoDir;
    }

    private static async Task RunGitCheckedAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitAsync(workingDirectory, arguments);
        result.ExitCode.Should().Be(0, result.Error);
    }

    private static async Task<string> RunGitForOutputAsync(string workingDirectory, params string[] arguments)
    {
        var result = await RunGitAsync(workingDirectory, arguments);
        result.ExitCode.Should().Be(0, result.Error);
        return result.Output.Trim();
    }

    private static async Task<GitCommandResult> RunGitAsync(string workingDirectory, params string[] arguments)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
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
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new GitCommandResult(
            process.ExitCode,
            await outputTask,
            await errorTask);
    }

    private static GitOpsWatchConfig CopyConfigWithPollState(
        GitOpsWatchConfig config,
        string commitSha,
        DateTimeOffset polledAt) => new()
        {
            ConfigId = config.ConfigId,
            RepositoryUrl = config.RepositoryUrl,
            Branch = config.Branch,
            ManifestPath = config.ManifestPath,
            PollIntervalSeconds = config.PollIntervalSeconds,
            ApprovalRequired = config.ApprovalRequired,
            PruneEnabled = config.PruneEnabled,
            Enabled = config.Enabled,
            LastKnownCommitSha = commitSha,
            LastPolledAt = polledAt,
            CreatedAt = config.CreatedAt,
            UpdatedAt = config.UpdatedAt,
            ConfiguredBy = config.ConfiguredBy
        };

    private sealed record GitCommandResult(int ExitCode, string Output, string Error);

    private sealed class TestGitOpsWatchStore(GitOpsWatchConfig config) : IGitOpsWatchStore
    {
        private GitOpsWatchConfig? _config = config;
        private readonly List<GitOpsChangeRecord> _changeRecords = [];

        public GitOpsWatchConfig CurrentConfig => _config ?? throw new InvalidOperationException("No config is stored.");
        public List<GitOpsChangeRecord> ChangeRecords => _changeRecords;
        public int PollStateUpdateCount { get; private set; }

        public Task<GitOpsWatchConfig> UpsertConfigAsync(GitOpsWatchConfig config, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<GitOpsWatchConfig?> GetConfigAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_config);

        public Task<bool> DeleteConfigAsync(Guid configId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UpdatePollStateAsync(
            Guid configId,
            string commitSha,
            DateTimeOffset polledAt,
            CancellationToken cancellationToken = default)
        {
            if (_config?.ConfigId != configId)
            {
                return Task.FromResult(false);
            }

            PollStateUpdateCount++;
            _config = CopyConfigWithPollState(_config, commitSha, polledAt);
            return Task.FromResult(true);
        }

        public Task<GitOpsChangeRecord> CreateChangeRecordAsync(
            GitOpsChangeRecord record,
            CancellationToken cancellationToken = default)
        {
            _changeRecords.Add(record);
            return Task.FromResult(record);
        }

        public Task<GitOpsChangeRecord?> GetChangeRecordAsync(Guid changeId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<GitOpsChangeRecord>> ListChangeRecordsAsync(
            int limit = 100,
            int offset = 0,
            CancellationToken cancellationToken = default)
        {
            var records = _changeRecords
                .OrderByDescending(record => record.DetectedAt)
                .Skip(offset)
                .Take(limit)
                .ToArray();

            return Task.FromResult<IReadOnlyList<GitOpsChangeRecord>>(records);
        }

        public Task<bool> UpdateChangeRecordByApprovalIdAsync(
            Guid pendingApprovalId,
            GitOpsChangeStatus newStatus,
            string? applySummary,
            string? errorMessage,
            DateTimeOffset? appliedAt,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestManifestPendingChangeStore : IManifestPendingChangeStore
    {
        private readonly List<ManifestPendingChange> _changes = [];

        public List<ManifestPendingChange> Changes => _changes;

        public Task<ManifestPendingChange> CreateAsync(
            ManifestPendingChange pendingChange,
            CancellationToken cancellationToken = default)
        {
            _changes.Add(pendingChange);
            return Task.FromResult(pendingChange);
        }

        public Task<ManifestPendingChange?> GetAsync(Guid pendingId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ManifestPendingChange>> ListAsync(
            ManifestApprovalStatus? status = null,
            int limit = 200,
            int offset = 0,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> UpdateDecisionAsync(
            Guid pendingId,
            ManifestApprovalStatus status,
            string? decisionBy,
            string? decisionReason,
            ManifestApprovalStatus expectedCurrentStatus = ManifestApprovalStatus.Pending,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<ManifestPendingChange>> ListExpiredAsync(
            DateTimeOffset asOf,
            int limit = 200,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
