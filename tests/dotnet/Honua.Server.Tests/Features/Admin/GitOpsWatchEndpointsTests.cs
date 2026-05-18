// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>
/// Integration tests for GitOps watch configuration and change management endpoints.
/// </summary>
[Protocol(TestProtocols.Admin)]
[Collection("Database")]
public sealed class GitOpsWatchEndpointsTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture;

    public GitOpsWatchEndpointsTests()
    {
        _fixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GitOpsWatch:Enabled"] = "true",
                    ["GitOpsWatch:MinPollIntervalSeconds"] = "30"
                });
            });
        });
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_CreatesNewConfig_Returns201()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/manifests.git",
            Branch = "main",
            ManifestPath = "deploy/",
            PollIntervalSeconds = 120,
            ApprovalRequired = false,
            Enabled = true,
            ConfiguredBy = "test-admin"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.Created);

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            GitOpsWatchJsonContext.Default.ApiResponseGitOpsWatchConfigResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Success.Should().BeTrue();
        apiResponse.Data.Should().NotBeNull();
        apiResponse.Data!.RepositoryUrl.Should().Be("https://github.com/example/manifests.git");
        apiResponse.Data.Branch.Should().Be("main");
        apiResponse.Data.ManifestPath.Should().Be("deploy/");
        apiResponse.Data.PollIntervalSeconds.Should().Be(120);
        apiResponse.Data.ApprovalRequired.Should().BeFalse();
        apiResponse.Data.ConfiguredBy.Should().Be("test-admin");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/gitops/watch")]
    public async Task GetWatch_WhenConfigured_ReturnsConfig()
    {
        var client = _fixture.CreateAdminClient();

        // First configure
        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/get-test.git",
            Branch = "develop",
            PollIntervalSeconds = 90,
            ConfiguredBy = "get-test-admin"
        };

        await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        // Then retrieve
        var getResponse = await client.GetAsync("/api/v1/admin/gitops/watch");
        getResponse.Be200Ok();

        var payload = await getResponse.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            GitOpsWatchJsonContext.Default.ApiResponseGitOpsWatchConfigResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.RepositoryUrl.Should().Contain("get-test.git");
        apiResponse.Data.Branch.Should().Be("develop");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("DELETE /api/v1/admin/gitops/watch")]
    public async Task DeleteWatch_RemovesConfig_Returns204()
    {
        // Use a separate fixture to avoid cross-test contamination
        var deleteFixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GitOpsWatch:Enabled"] = "true"
                });
            });
        });

        await deleteFixture.InitializeAsync();
        try
        {
            var client = deleteFixture.CreateAdminClient();

            // Configure
            var request = new GitOpsWatchConfigRequest
            {
                RepositoryUrl = "https://github.com/example/delete-test.git",
                Branch = "main",
                ConfiguredBy = "delete-test-admin"
            };

            await client.PostAsync(
                "/api/v1/admin/gitops/watch",
                JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

            // Delete
            var deleteResponse = await client.DeleteAsync("/api/v1/admin/gitops/watch");
            deleteResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);

            // Verify gone
            var getResponse = await client.GetAsync("/api/v1/admin/gitops/watch");
            getResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally
        {
            await deleteFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("PUT /api/v1/admin/gitops/watch")]
    public async Task UpdateWatch_ModifiesExistingConfig()
    {
        var client = _fixture.CreateAdminClient();

        // Configure initial
        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/update-test.git",
            Branch = "main",
            PollIntervalSeconds = 60,
            ApprovalRequired = false,
            ConfiguredBy = "update-test-admin"
        };

        await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        // Update
        var updateRequest = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/update-test.git",
            Branch = "release",
            PollIntervalSeconds = 300,
            ApprovalRequired = true,
            ConfiguredBy = "update-test-admin"
        };

        var updateResponse = await client.PutAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(updateRequest, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        updateResponse.Be200Ok();

        var payload = await updateResponse.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            GitOpsWatchJsonContext.Default.ApiResponseGitOpsWatchConfigResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.Branch.Should().Be("release");
        apiResponse.Data.PollIntervalSeconds.Should().Be(300);
        apiResponse.Data.ApprovalRequired.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_EnforcesPollIntervalFloor()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/floor-test.git",
            Branch = "main",
            PollIntervalSeconds = 5 // Below minimum of 30
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            GitOpsWatchJsonContext.Default.ApiResponseGitOpsWatchConfigResponse);

        apiResponse!.Data!.PollIntervalSeconds.Should().BeGreaterOrEqualTo(30);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_MissingUrl_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "",
            Branch = "main"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/gitops/changes")]
    public async Task ListChanges_ReturnsChangeHistory()
    {
        // Seed a change record via the store directly
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGitOpsWatchStore>();

        // Ensure a config exists
        var config = await store.GetConfigAsync();
        if (config == null)
        {
            config = await store.UpsertConfigAsync(new GitOpsWatchConfig
            {
                ConfigId = Guid.NewGuid(),
                RepositoryUrl = "https://github.com/example/changes-test.git",
                Branch = "main",
                ManifestPath = "manifests/",
                PollIntervalSeconds = 60,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        var manifestContent = JsonSerializer.SerializeToElement(new[] { new { kind = "Layer", metadata = new { name = "test" } } });

        await store.CreateChangeRecordAsync(new GitOpsChangeRecord
        {
            ChangeId = Guid.NewGuid(),
            ConfigId = config.ConfigId,
            CommitSha = "abc123def456",
            CommitMessage = "Add layer config",
            CommitAuthor = "developer@example.com",
            CommitTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5),
            ManifestAfter = manifestContent,
            Status = GitOpsChangeStatus.Applied,
            ApplySummary = "Created: 1, Updated: 0, Deleted: 0, Skipped: 0",
            DetectedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            AppliedAt = DateTimeOffset.UtcNow.AddMinutes(-5)
        });

        var client = _fixture.CreateAdminClient();
        var response = await client.GetAsync("/api/v1/admin/gitops/changes");
        response.Be200Ok();

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            GitOpsWatchJsonContext.Default.ApiResponseGitOpsChangeRecordResponseArray);

        apiResponse.Should().NotBeNull();
        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.Should().Contain(c => c.CommitSha == "abc123def456");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task GitOpsWatchStore_CommitProcessingLease_AllowsOneActiveProcessor()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGitOpsWatchStore>();
        var config = await store.UpsertConfigAsync(new GitOpsWatchConfig
        {
            ConfigId = Guid.NewGuid(),
            RepositoryUrl = "https://github.com/example/lease-test.git",
            Branch = "main",
            ManifestPath = "manifests/",
            PollIntervalSeconds = 60,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        const string commitSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var now = DateTimeOffset.UtcNow;
        var firstLeaseId = Guid.NewGuid();
        var secondLeaseId = Guid.NewGuid();

        var firstAcquired = await store.TryAcquireCommitProcessingLeaseAsync(
            config.ConfigId,
            commitSha,
            firstLeaseId,
            now,
            now.AddMinutes(5));
        var secondAcquired = await store.TryAcquireCommitProcessingLeaseAsync(
            config.ConfigId,
            commitSha,
            secondLeaseId,
            now.AddSeconds(1),
            now.AddMinutes(5));

        firstAcquired.Should().BeTrue();
        secondAcquired.Should().BeFalse();

        var completed = await store.CompleteCommitProcessingAsync(
            config.ConfigId,
            commitSha,
            firstLeaseId,
            now.AddSeconds(2));

        completed.Should().BeTrue();
        var currentConfig = await store.GetConfigAsync();
        currentConfig!.LastKnownCommitSha.Should().Be(commitSha);

        var alreadyObservedAcquire = await store.TryAcquireCommitProcessingLeaseAsync(
            config.ConfigId,
            commitSha,
            Guid.NewGuid(),
            now.AddSeconds(3),
            now.AddMinutes(6));

        alreadyObservedAcquire.Should().BeFalse();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task GitOpsWatchStore_ExpiredCommitProcessingLease_CanBeReplacedByNewOwner()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGitOpsWatchStore>();
        var config = await store.UpsertConfigAsync(new GitOpsWatchConfig
        {
            ConfigId = Guid.NewGuid(),
            RepositoryUrl = "https://github.com/example/expired-lease-test.git",
            Branch = "main",
            ManifestPath = "manifests/",
            PollIntervalSeconds = 60,
            Enabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });

        const string commitSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var now = DateTimeOffset.UtcNow;
        var expiredLeaseId = Guid.NewGuid();
        var replacementLeaseId = Guid.NewGuid();

        var expiredAcquired = await store.TryAcquireCommitProcessingLeaseAsync(
            config.ConfigId,
            commitSha,
            expiredLeaseId,
            now.AddMinutes(-10),
            now.AddMinutes(-5));
        var replacementAcquired = await store.TryAcquireCommitProcessingLeaseAsync(
            config.ConfigId,
            commitSha,
            replacementLeaseId,
            now,
            now.AddMinutes(5));

        expiredAcquired.Should().BeTrue();
        replacementAcquired.Should().BeTrue();

        var expiredComplete = await store.CompleteCommitProcessingAsync(
            config.ConfigId,
            commitSha,
            expiredLeaseId,
            now.AddSeconds(1));
        var replacementComplete = await store.CompleteCommitProcessingAsync(
            config.ConfigId,
            commitSha,
            replacementLeaseId,
            now.AddSeconds(2));

        expiredComplete.Should().BeFalse();
        replacementComplete.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/gitops/changes/{id}")]
    public async Task GetChange_ReturnsSingleChange()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGitOpsWatchStore>();

        var config = await store.GetConfigAsync();
        if (config == null)
        {
            config = await store.UpsertConfigAsync(new GitOpsWatchConfig
            {
                ConfigId = Guid.NewGuid(),
                RepositoryUrl = "https://github.com/example/single-test.git",
                Branch = "main",
                ManifestPath = "manifests/",
                PollIntervalSeconds = 60,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        var manifestContent = JsonSerializer.SerializeToElement(new[] { new { kind = "Layer", metadata = new { name = "single" } } });
        var changeId = Guid.NewGuid();

        await store.CreateChangeRecordAsync(new GitOpsChangeRecord
        {
            ChangeId = changeId,
            ConfigId = config.ConfigId,
            CommitSha = "single123",
            CommitMessage = "Single change test",
            CommitAuthor = "tester",
            ManifestAfter = manifestContent,
            Status = GitOpsChangeStatus.Applied,
            DetectedAt = DateTimeOffset.UtcNow
        });

        var client = _fixture.CreateAdminClient();
        var response = await client.GetAsync($"/api/v1/admin/gitops/changes/{changeId}");
        response.Be200Ok();

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            GitOpsWatchJsonContext.Default.ApiResponseGitOpsChangeRecordResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.ChangeId.Should().Be(changeId);
        apiResponse.Data.CommitSha.Should().Be("single123");
        apiResponse.Data.Status.Should().Be("applied");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/gitops/changes/{id}/diff")]
    public async Task GetChangeDiff_ReturnsBeforeAfterManifest()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGitOpsWatchStore>();

        var config = await store.GetConfigAsync();
        if (config == null)
        {
            config = await store.UpsertConfigAsync(new GitOpsWatchConfig
            {
                ConfigId = Guid.NewGuid(),
                RepositoryUrl = "https://github.com/example/diff-test.git",
                Branch = "main",
                ManifestPath = "manifests/",
                PollIntervalSeconds = 60,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        var before = JsonSerializer.SerializeToElement(new[] { new { kind = "Layer", metadata = new { name = "old" } } });
        var after = JsonSerializer.SerializeToElement(new[] { new { kind = "Layer", metadata = new { name = "new" } } });
        var changeId = Guid.NewGuid();

        await store.CreateChangeRecordAsync(new GitOpsChangeRecord
        {
            ChangeId = changeId,
            ConfigId = config.ConfigId,
            CommitSha = "diff456",
            CommitMessage = "Diff test change",
            ManifestBefore = before,
            ManifestAfter = after,
            Status = GitOpsChangeStatus.Applied,
            DetectedAt = DateTimeOffset.UtcNow
        });

        var client = _fixture.CreateAdminClient();
        var response = await client.GetAsync($"/api/v1/admin/gitops/changes/{changeId}/diff");
        response.Be200Ok();

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            GitOpsWatchJsonContext.Default.ApiResponseGitOpsChangeDiffResponse);

        apiResponse.Should().NotBeNull();
        apiResponse!.Data.Should().NotBeNull();
        apiResponse.Data!.ChangeId.Should().Be(changeId);
        apiResponse.Data.CommitSha.Should().Be("diff456");
        apiResponse.Data.Before.Should().NotBeNull();
        apiResponse.Data.After.ValueKind.Should().Be(JsonValueKind.Array);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/gitops/watch")]
    public async Task GetWatch_WhenDisabled_Returns403()
    {
        var disabledFixture = new WebAppFixture().ConfigureWebHost(builder =>
        {
            builder.ConfigureAppConfiguration((_, configBuilder) =>
            {
                configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["GitOpsWatch:Enabled"] = "false"
                });
            });
        });

        await disabledFixture.InitializeAsync();
        try
        {
            var client = disabledFixture.CreateAdminClient();
            var response = await client.GetAsync("/api/v1/admin/gitops/watch");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        finally
        {
            await disabledFixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/gitops/changes/{id}")]
    public async Task GetChange_NotFound_Returns404()
    {
        var client = _fixture.CreateAdminClient();
        var response = await client.GetAsync($"/api/v1/admin/gitops/changes/{Guid.NewGuid()}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_UrlStartsWithDash_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "--upload-pack=evil",
            Branch = "main"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_MalformedRepositoryUrl_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/repo.git invalid",
            Branch = "main"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_HttpRepositoryUrl_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "http://github.com/example/repo.git",
            Branch = "main"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_RepositoryUrlWithEmbeddedCredentials_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://user:password@github.com/example/repo.git",
            Branch = "main"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_ScpStyleSshRepositoryUrl_Returns201()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "git@github.com:example/repo.git",
            Branch = "main",
            ManifestPath = "deploy/",
            PollIntervalSeconds = 120,
            Enabled = true
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_BranchWithInvalidChars_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/repo.git",
            Branch = "--option-injection"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_ManifestPathTraversal_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/repo.git",
            Branch = "main",
            ManifestPath = "/etc/passwd"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_ManifestPathDotDot_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/repo.git",
            Branch = "main",
            ManifestPath = "manifests/../../etc/passwd"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_ManifestPathGlob_Returns400()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/repo.git",
            Branch = "main",
            ManifestPath = "manifests/*.json"
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().Contain("glob patterns are not supported");
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("POST /api/v1/admin/gitops/watch")]
    public async Task ConfigureWatch_PruneEnabledRoundTrips()
    {
        var client = _fixture.CreateAdminClient();

        var request = new GitOpsWatchConfigRequest
        {
            RepositoryUrl = "https://github.com/example/prune-test.git",
            Branch = "main",
            PruneEnabled = true,
            Enabled = true
        };

        var response = await client.PostAsync(
            "/api/v1/admin/gitops/watch",
            JsonContent.Create(request, GitOpsWatchJsonContext.Default.GitOpsWatchConfigRequest));

        var payload = await response.Content.ReadAsStringAsync();
        var apiResponse = JsonSerializer.Deserialize(
            payload,
            GitOpsWatchJsonContext.Default.ApiResponseGitOpsWatchConfigResponse);

        apiResponse!.Data!.PruneEnabled.Should().BeTrue();
    }

    [IntegrationTest]
    [Operation(Operations.Metadata)]
    [Endpoint("GET /api/v1/admin/gitops/changes/{id}")]
    public async Task ApprovalDecision_UpdatesGitOpsChangeRecord()
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IGitOpsWatchStore>();

        var config = await store.GetConfigAsync();
        if (config == null)
        {
            config = await store.UpsertConfigAsync(new GitOpsWatchConfig
            {
                ConfigId = Guid.NewGuid(),
                RepositoryUrl = "https://github.com/example/approval-test.git",
                Branch = "main",
                ManifestPath = "manifests/",
                PollIntervalSeconds = 60,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        var pendingId = Guid.NewGuid();
        var changeId = Guid.NewGuid();
        var manifestContent = JsonSerializer.SerializeToElement(new[] { new { kind = "Layer", metadata = new { name = "approval-test" } } });

        await store.CreateChangeRecordAsync(new GitOpsChangeRecord
        {
            ChangeId = changeId,
            ConfigId = config.ConfigId,
            CommitSha = "approval123",
            CommitMessage = "Approval test",
            ManifestAfter = manifestContent,
            Status = GitOpsChangeStatus.PendingApproval,
            PendingApprovalId = pendingId,
            DetectedAt = DateTimeOffset.UtcNow
        });

        // Simulate what the approval handler does: update via PendingApprovalId
        var updated = await store.UpdateChangeRecordByApprovalIdAsync(
            pendingId,
            GitOpsChangeStatus.Applied,
            "Created: 1, Updated: 0, Deleted: 0, Skipped: 0",
            errorMessage: null,
            appliedAt: DateTimeOffset.UtcNow);

        updated.Should().BeTrue();

        // Verify the change record is now Applied
        var record = await store.GetChangeRecordAsync(changeId);
        record.Should().NotBeNull();
        record!.Status.Should().Be(GitOpsChangeStatus.Applied);
        record.ApplySummary.Should().Contain("Created: 1");
        record.AppliedAt.Should().NotBeNull();
    }
}
