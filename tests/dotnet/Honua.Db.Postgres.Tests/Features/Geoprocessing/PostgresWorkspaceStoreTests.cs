// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Data.Common;
using DbUp;
using DbUp.Helpers;
using Honua.Db.Postgres.Features.Infrastructure;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Npgsql;
using NSubstitute;

namespace Honua.Db.Postgres.Tests.Features.Geoprocessing;

[Collection("Database")]
public sealed class PostgresWorkspaceStoreTests(PostgresFixture fixture)
{
    [IntegrationTest]
    public Task Workspace_ExpiredActiveRowsReleaseQuotaWithoutCleanup()
        => WithStoresAsync(async stores =>
        {
            var original = await stores[0].GetOrCreateNamedAsync(Workspace("owner", "analysis"), 1);
            await stores[0].CreateAsync(Artifact(original.WorkspaceId, "output", "data:text/plain,old") with { SizeBytes = 3 });
            var overLimit = () => stores[1].GetOrCreateNamedAsync(Workspace("owner", "replacement"), 1);
            await overLimit.Should().ThrowAsync<WorkspaceQuotaExceededException>();

            // Leave the persisted state Active: no cleanup service or sweep runs.
            await stores[0].ExtendExpirationAsync(original.WorkspaceId, DateTimeOffset.UtcNow.AddMinutes(-1));
            (await stores[1].GetAsync(original.WorkspaceId))!.State.Should().Be(WorkspaceLifecycleState.Active);
            var expiredUsage = await stores[1].GetUsageSummaryAsync("owner");
            expiredUsage.ActiveWorkspaceCount.Should().Be(0);
            expiredUsage.TotalArtifactCount.Should().Be(0);
            expiredUsage.TotalStorageBytes.Should().Be(0);

            var replacement = await stores[2].GetOrCreateNamedAsync(Workspace("owner", "analysis"), 1);
            replacement.WorkspaceId.Should().NotBe(original.WorkspaceId);
            (await stores[3].GetUsageSummaryAsync("owner")).ActiveWorkspaceCount.Should().Be(1);
            (await stores[3].ListByOwnerAsync("owner")).Should().HaveCount(2);
        });

    [IntegrationTest]
    public Task Workspace_CountQuotaSerializesDifferentLabelsScopesAndCreationPaths()
        => WithStoresAsync(async stores =>
        {
            var attempts = await Task.WhenAll(Enumerable.Range(0, 16).Select(async i =>
            {
                var proposal = Workspace("owner", "quota-" + i) with { ScopeId = i % 2 == 0 ? null : "optional" };
                try
                {
                    return i % 2 == 0
                        ? await stores[i % stores.Length].GetOrCreateNamedAsync(proposal, 3)
                        : await stores[i % stores.Length].CreateWithQuotaAsync(proposal, 3);
                }
                catch (WorkspaceQuotaExceededException)
                {
                    return null;
                }
            }));
            var created = attempts.OfType<Workspace>().ToArray();
            created.Should().HaveCount(3);
            (await stores[0].GetUsageSummaryAsync("owner")).ActiveWorkspaceCount.Should().Be(3);
            (await stores[1].GetOrCreateNamedAsync(created[0] with { WorkspaceId = "unused" }, 3))
                .WorkspaceId.Should().Be(created[0].WorkspaceId);
            await stores[2].GetOrCreateNamedAsync(Workspace("another-owner", "quota"), 3);
            await stores[2].TransitionStateAsync(created[0].WorkspaceId, WorkspaceLifecycleState.Expired);
            await stores[3].GetOrCreateNamedAsync(Workspace("owner", "replacement"), 3);
            (await stores[0].GetUsageSummaryAsync("owner")).ActiveWorkspaceCount.Should().Be(3);
        });

    [IntegrationTest]
    public Task NamedWorkspace_ConcurrentProvidersShareOneDurableIdentity()
        => WithStoresAsync(async stores =>
        {
            var proposal = Workspace("alice", "analysis");
            var workspaces = await Task.WhenAll(Enumerable.Range(0, 16).Select(i =>
                stores[i % stores.Length].GetOrCreateNamedAsync(proposal with { WorkspaceId = Guid.NewGuid().ToString("N") })));
            workspaces.Select(w => w.WorkspaceId).Distinct().Should().ContainSingle();
            var reread = await stores[3].GetAsync(workspaces[0].WorkspaceId);
            reread.Should().NotBeNull();
            reread!.OwnerId.Should().Be("alice");
            reread.Label.Should().Be("analysis");
            (await stores[3].ListByOwnerAsync("alice")).Should().ContainSingle();
        });

    [IntegrationTest]
    public Task NamedWorkspace_SeparatesOwnerOptionalScopeAndExpiredRecords()
        => WithStoresAsync(async stores =>
        {
            var template = Workspace("alice", "分析");
            var first = await stores[0].GetOrCreateNamedAsync(template);
            var otherOwner = await stores[0].GetOrCreateNamedAsync(template with { WorkspaceId = "other-owner", OwnerId = "bob" });
            var scoped = await stores[0].GetOrCreateNamedAsync(template with { WorkspaceId = "scoped", ScopeId = "existing-scope" });
            var unscoped = await stores[1].GetOrCreateNamedAsync(template with { WorkspaceId = "unused" });
            unscoped.WorkspaceId.Should().Be(first.WorkspaceId);
            new[] { first.WorkspaceId, otherOwner.WorkspaceId, scoped.WorkspaceId }.Should().OnlyHaveUniqueItems();
            await stores[0].ExtendExpirationAsync(first.WorkspaceId, DateTimeOffset.UtcNow.AddMinutes(-1));
            var replacement = await stores[2].GetOrCreateNamedAsync(template with { WorkspaceId = "fresh" });
            replacement.WorkspaceId.Should().Be("fresh");
            (await stores[0].ListExpiredAsync(DateTimeOffset.UtcNow, 1)).Should().ContainSingle().Which.WorkspaceId.Should().Be(first.WorkspaceId);
        });

    [IntegrationTest]
    public Task Artifact_ConcurrentNonOverwriteHasOneWinnerAndExactContent()
        => WithStoresAsync(async stores =>
        {
            var workspace = await stores[0].CreateAsync(Workspace("owner", "scratch"));
            var template = Artifact(workspace.WorkspaceId, "Résumé", "data:application/json,{\"count\":7}") with
            {
                SizeBytes = 42,
                Metadata = new Dictionary<string, string> { ["unicode"] = "日本語", ["empty"] = "" }
            };
            var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i =>
                stores[i % stores.Length].AddOrReplaceAsync(template with { ArtifactId = Guid.NewGuid().ToString("N"), Label = i % 2 == 0 ? "Résumé" : "RÉSUMÉ" }, false)));
            var winner = results.Where(a => a is not null).Should().ContainSingle().Subject!;
            var reread = await ((IArtifactStore)stores[3]).GetAsync(winner.ArtifactId);
            reread.Should().BeEquivalentTo(winner);
            var usage = await stores[3].GetUsageSummaryAsync("owner");
            usage.ActiveWorkspaceCount.Should().Be(1);
            usage.TotalArtifactCount.Should().Be(1);
            usage.TotalStorageBytes.Should().Be(42);
            (await stores[3].GetAsync(workspace.WorkspaceId))!.Artifacts.Should().ContainSingle();
            (await stores[3].GetAsync(workspace.WorkspaceId))!.StorageBytes.Should().Be(42);
            (await stores[3].ListByOwnerAsync("owner")).Should().ContainSingle().Which.StorageBytes.Should().Be(42);
        });

    [IntegrationTest]
    public Task Artifact_OverwriteFailureRollsBackThenSuccessfulReplacementPersists()
        => WithStoresAsync(async stores =>
        {
            var workspace = await stores[0].CreateAsync(Workspace("owner", "scratch"));
            var original = Artifact(workspace.WorkspaceId, "output", "data:text/plain,original") with { SizeBytes = 8 };
            await stores[0].AddOrReplaceAsync(original, false);
            var other = Artifact(workspace.WorkspaceId, "different-output", "data:text/plain,other");
            await stores[0].CreateAsync(other);
            var fail = () => stores[1].AddOrReplaceAsync(original with { ArtifactId = other.ArtifactId, Uri = "data:text/plain,failed" }, true);
            await fail.Should().ThrowAsync<PostgresException>().Where(e => e.SqlState == PostgresErrorCodes.UniqueViolation);
            (await ((IArtifactStore)stores[2]).GetAsync(original.ArtifactId)).Should().BeEquivalentTo(original);
            var replacement = original with { ArtifactId = "replacement", Uri = "data:text/plain,new", SizeBytes = 3 };
            await stores[2].AddOrReplaceAsync(replacement, true);
            (await ((IArtifactStore)stores[3]).GetAsync(original.ArtifactId)).Should().BeNull();
            (await ((IArtifactStore)stores[3]).GetAsync(replacement.ArtifactId)).Should().BeEquivalentTo(replacement);
            (await stores[3].GetUsageSummaryAsync("owner")).TotalStorageBytes.Should().Be(3);
        });

    [IntegrationTest]
    public Task Artifact_ExpiredWorkspaceRejectsWriteAndWorkspaceDeletionRequiresCleanup()
        => WithStoresAsync(async stores =>
        {
            var workspace = await stores[0].CreateAsync(Workspace("owner", "scratch"));
            var artifact = await stores[0].CreateAsync(Artifact(workspace.WorkspaceId, "output", "https://example.invalid/owned-by-another-provider"));
            (await stores[1].DeleteAsync(workspace.WorkspaceId)).Should().BeFalse();
            await stores[0].ExtendExpirationAsync(workspace.WorkspaceId, DateTimeOffset.UtcNow.AddSeconds(-1));
            var write = () => stores[1].AddOrReplaceAsync(artifact with { ArtifactId = "replacement" }, true);
            await write.Should().ThrowAsync<InvalidOperationException>();
            (await ((IArtifactStore)stores[2]).GetAsync(artifact.ArtifactId)).Should().BeEquivalentTo(artifact);
            (await ((IArtifactStore)stores[2]).DeleteAsync(artifact.ArtifactId)).Should().BeTrue();
            (await stores[2].DeleteAsync(workspace.WorkspaceId)).Should().BeTrue();
            (await stores[3].GetAsync(workspace.WorkspaceId)).Should().BeNull();
        });

    [IntegrationTest]
    public Task Artifact_NegativeSizeAndCancelledWritePreserveExistingOutput()
        => WithStoresAsync(async stores =>
        {
            var workspace = await stores[0].CreateAsync(Workspace("owner", "scratch"));
            var original = await stores[0].CreateAsync(Artifact(workspace.WorkspaceId, "output", "data:text/plain,original"));
            var invalid = () => stores[1].AddOrReplaceAsync(original with { SizeBytes = -1 }, true);
            await invalid.Should().ThrowAsync<ArgumentOutOfRangeException>();
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            var cancelled = () => stores[1].AddOrReplaceAsync(original with { ArtifactId = "cancelled" }, true, cancellation.Token);
            await cancelled.Should().ThrowAsync<OperationCanceledException>();
            (await ((IArtifactStore)stores[2]).GetAsync(original.ArtifactId)).Should().BeEquivalentTo(original);
        });

    private async Task WithStoresAsync(Func<PostgresWorkspaceStore[], Task> action)
    {
        var schema = await fixture.CreateIsolatedSchemaAsync(nameof(PostgresWorkspaceStoreTests));
        try
        {
            var migration = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Migrations", "118_CreateGeoprocessingWorkspaces.sql"));
            var upgrader = DeployChanges.To.PostgresqlDatabase(fixture.ConnectionString)
                .JournalTo(new NullJournal())
                .WithScript("118_CreateGeoprocessingWorkspaces.sql", migration)
                .WithVariable("HonuaSchema", SchemaSearchPath.ValidateAndQuote(schema))
                .WithTransaction().Build();
            upgrader.PerformUpgrade().Successful.Should().BeTrue();
            upgrader.PerformUpgrade().Successful.Should().BeTrue(); // Actual DbUp substitution and idempotence.
            var stores = Enumerable.Range(0, 4).Select(_ =>
            {
                var provider = Substitute.For<IAdoNetDatabaseConnectionProvider>();
                provider.OpenConnectionAsync(Arg.Any<CancellationToken>()).Returns(async call =>
                    (DbConnection)await fixture.DataSource.OpenConnectionAsync(call.Arg<CancellationToken>()));
                return new PostgresWorkspaceStore(provider, schema);
            }).ToArray();
            await action(stores);
        }
        finally
        {
            await fixture.DropSchemaAsync(schema);
        }
    }

    private static Workspace Workspace(string owner, string label) => new()
    {
        WorkspaceId = Guid.NewGuid().ToString("N"),
        Kind = WorkspaceKind.Scratch,
        OwnerId = owner,
        Label = label,
        State = WorkspaceLifecycleState.Active,
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
        ExpiresAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeMilliseconds())
    };

    private static Artifact Artifact(string workspace, string label, string uri) => new()
    {
        ArtifactId = Guid.NewGuid().ToString("N"),
        WorkspaceId = workspace,
        Kind = ArtifactKind.File,
        Label = label,
        State = ArtifactLifecycleState.Available,
        Uri = uri,
        ContentType = "text/plain",
        CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
    };
}
