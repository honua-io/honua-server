// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Identity.Domain;
using Honua.Core.Features.RateLimiting.Domain;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class ConfigurationDocumentRevisionTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string RateKind = PostgresRateLimitPolicyStore.DocumentKind;

    [IntegrationTest]
    public async Task UndoAsync_AfterBadUpdate_RestoresPriorValueAsNewerRevision()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateRateStore();
        var policy = await store.CreatePolicyAsync(Policy(10));
        (await store.ListPoliciesAsync()).Should().ContainSingle();
        await store.UpdatePolicyAsync(policy.PolicyId, current => Policy(20, current.PolicyId));
        var documents = harness.CreateConfigurationDocuments();
        var bad = (await documents.ListRevisionsAsync(Document(policy), default))[0];

        var result = await documents.UndoAsync(Undo(Document(policy), bad.RevisionId, DeletedDocumentUndo.Recreate), default);

        result.Outcome.Should().Be(ConfigurationUndoOutcome.Undone);
        result.Revision!.RevisionId.Should().BeGreaterThan(bad.RevisionId);
        result.Revision.Generation.Should().BeGreaterThan(bad.Generation);
        result.Revision.RestoredFrom.Should().Be(bad.RevisionId);
        result.Revision.Change.Should().Be("updated");
        result.Revision.DocumentRevision.Should().Be(3);
        (await store.ListPoliciesAsync()).Should().ContainSingle().Which.RequestsPerWindow.Should().Be(10);
    }

    [IntegrationTest]
    public async Task UndoAsync_AfterLaterChange_IsSupersededAndKeepsTheLaterChange()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateRateStore();
        var policy = await store.CreatePolicyAsync(Policy(10));
        await store.UpdatePolicyAsync(policy.PolicyId, current => Policy(20, current.PolicyId));
        var documents = harness.CreateConfigurationDocuments();
        var seen = (await documents.ListRevisionsAsync(Document(policy), default))[0];
        await store.UpdatePolicyAsync(policy.PolicyId, current => Policy(30, current.PolicyId));

        var result = await documents.UndoAsync(Undo(Document(policy), seen.RevisionId, DeletedDocumentUndo.Recreate), default);

        result.Outcome.Should().Be(ConfigurationUndoOutcome.Superseded);
        (await store.GetPolicyAsync(policy.PolicyId))!.RequestsPerWindow.Should().Be(30);
        (await documents.ListRevisionsAsync(Document(policy), default)).Should().HaveCount(3);
    }

    [IntegrationTest]
    public async Task UndoAsync_OfCreate_DeletesTheDocument()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateRateStore();
        var policy = await store.CreatePolicyAsync(Policy(10));
        var documents = harness.CreateConfigurationDocuments();
        var created = (await documents.ListRevisionsAsync(Document(policy), default)).Should().ContainSingle().Subject;

        var result = await documents.UndoAsync(Undo(Document(policy), created.RevisionId, DeletedDocumentUndo.Refuse), default);

        result.Outcome.Should().Be(ConfigurationUndoOutcome.Undone);
        result.Revision!.Change.Should().Be("deleted");
        result.Revision.DocumentRevision.Should().BeNull();
        (await store.GetPolicyAsync(policy.PolicyId)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task UndoAsync_OfDelete_RecreatesOnlyWhenAllowed()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateRateStore();
        var policy = await store.CreatePolicyAsync(Policy(5));
        await store.UpdatePolicyAsync(policy.PolicyId, current => Policy(10, current.PolicyId));
        await store.DeletePolicyAsync(policy.PolicyId);
        var documents = harness.CreateConfigurationDocuments();
        var deleted = (await documents.ListRevisionsAsync(Document(policy), default))[0];

        (await documents.UndoAsync(Undo(Document(policy), deleted.RevisionId, DeletedDocumentUndo.Refuse), default))
            .Outcome.Should().Be(ConfigurationUndoOutcome.RecreateRefused);
        (await store.GetPolicyAsync(policy.PolicyId)).Should().BeNull();

        var result = await documents.UndoAsync(Undo(Document(policy), deleted.RevisionId, DeletedDocumentUndo.Recreate), default);

        result.Outcome.Should().Be(ConfigurationUndoOutcome.Undone);
        result.Revision!.Change.Should().Be("created");
        result.Revision.DocumentRevision.Should().Be(3, "a re-created document must not reuse a revision a stale writer may hold");
        (await store.GetPolicyAsync(policy.PolicyId))!.RequestsPerWindow.Should().Be(10);
    }

    [IntegrationTest]
    public async Task UndoAsync_ConcurrentUndosOfTheSameChange_ApplyExactlyOnce()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateRateStore();
        var policy = await store.CreatePolicyAsync(Policy(10));
        await store.UpdatePolicyAsync(policy.PolicyId, current => Policy(20, current.PolicyId));
        var bad = (await harness.CreateConfigurationDocuments().ListRevisionsAsync(Document(policy), default))[0];

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => harness.CreateConfigurationDocuments()
            .UndoAsync(Undo(Document(policy), bad.RevisionId, DeletedDocumentUndo.Recreate), default)));

        results.Count(result => result.Outcome == ConfigurationUndoOutcome.Undone).Should().Be(1);
        results.Count(result => result.Outcome == ConfigurationUndoOutcome.Superseded).Should().Be(3);
        (await store.GetPolicyAsync(policy.PolicyId))!.RequestsPerWindow.Should().Be(10);
    }

    [IntegrationTest]
    public async Task UndoAsync_OfAnUndo_ReappliesTheUndoneChange()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateRateStore();
        var policy = await store.CreatePolicyAsync(Policy(10));
        await store.UpdatePolicyAsync(policy.PolicyId, current => Policy(20, current.PolicyId));
        var documents = harness.CreateConfigurationDocuments();
        var bad = (await documents.ListRevisionsAsync(Document(policy), default))[0];
        var undo = (await documents.UndoAsync(Undo(Document(policy), bad.RevisionId, DeletedDocumentUndo.Recreate), default)).Revision!;

        var redo = await documents.UndoAsync(Undo(Document(policy), undo.RevisionId, DeletedDocumentUndo.Recreate), default);

        redo.Outcome.Should().Be(ConfigurationUndoOutcome.Undone);
        redo.Revision!.RestoredFrom.Should().Be(undo.RevisionId);
        (await store.GetPolicyAsync(policy.PolicyId))!.RequestsPerWindow.Should().Be(20);
    }

    [IntegrationTest]
    public async Task UndoAsync_DocumentWrittenBeforeHistory_HasNothingToUndo()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateRateStore();
        var policy = await store.CreatePolicyAsync(Policy(10));
        await harness.ExecuteAsync($"DELETE FROM {harness.Table("configuration_document_revisions")}");

        await harness.ReplayRevisionMigrationAsync();
        await harness.ReplayRevisionMigrationAsync();

        var documents = harness.CreateConfigurationDocuments();
        var baseline = (await documents.ListRevisionsAsync(Document(policy), default)).Should().ContainSingle().Subject;
        baseline.Change.Should().Be("baseline");
        (await documents.UndoAsync(Undo(Document(policy), baseline.RevisionId, DeletedDocumentUndo.Recreate), default))
            .Outcome.Should().Be(ConfigurationUndoOutcome.NothingToUndo);
        (await store.GetPolicyAsync(policy.PolicyId))!.RequestsPerWindow.Should().Be(10);
    }

    [IntegrationTest]
    public async Task Revisions_ManyChanges_KeepTheNewestThirtyTwoPerDocument()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateRateStore();
        var policy = await store.CreatePolicyAsync(Policy(1));
        var other = await store.CreatePolicyAsync(Policy(1));
        for (var limit = 2; limit <= 40; limit++)
        {
            var next = limit;
            await store.UpdatePolicyAsync(policy.PolicyId, current => Policy(next, current.PolicyId));
        }

        var revisions = await harness.CreateConfigurationDocuments().ListRevisionsAsync(Document(policy), default);

        revisions.Should().HaveCount(PostgresConfigurationDocuments.RevisionsKept);
        revisions.Should().BeInDescendingOrder(revision => revision.RevisionId);
        revisions[0].DocumentRevision.Should().Be(40);
        (await harness.ScalarAsync<long>(
            $"SELECT count(*) FROM {harness.Table("configuration_document_revisions")} WHERE key = '{Key(other)}'")).Should().Be(1);
    }

    [IntegrationTest]
    public async Task UndoAsync_OidcProviderDisabled_RestoresProviderWithItsSecret()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateOidcStore();
        var provider = await store.CreateProviderAsync(EnabledProvider());
        await store.UpdateProviderAsync(provider.ProviderId, _ => DisabledProvider(provider.ProviderId));
        var documents = harness.CreateConfigurationDocuments();
        var key = new ConfigurationDocumentKey(PostgresOidcProviderStore.DocumentKind, provider.ProviderId.ToString("D"));
        var disabled = (await documents.ListRevisionsAsync(key, default))[0];

        var result = await documents.UndoAsync(Undo(key, disabled.RevisionId, DeletedDocumentUndo.Refuse), default);

        result.Outcome.Should().Be(ConfigurationUndoOutcome.Undone);
        var restored = await harness.CreateOidcStore().GetProviderAsync(provider.ProviderId);
        restored!.Enabled.Should().BeTrue();
        restored.ClientSecret.Should().Be("private-client-secret");
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(ConfigurationDocumentRevisionTests));

    private static string Key(RateLimitPolicy policy) => policy.PolicyId.ToString("D");

    private static ConfigurationDocumentKey Document(RateLimitPolicy policy) => new(RateKind, Key(policy));

    private static ConfigurationUndoRequest Undo(ConfigurationDocumentKey document, long expectedRevisionId, DeletedDocumentUndo deleted)
        => new(new(document, deleted), expectedRevisionId);

    private static RateLimitPolicy Policy(int limit, Guid? id = null) => new()
    {
        PolicyId = id ?? Guid.NewGuid(),
        Name = "Tenant",
        Scope = RateLimitScopes.Tenant,
        Key = "tenant-a",
        RequestsPerWindow = limit,
        WindowDuration = TimeSpan.FromMinutes(1)
    };

    private static OidcProviderConfiguration EnabledProvider() => new()
    {
        ProviderId = Guid.NewGuid(),
        Name = "Example",
        ProviderType = "Generic",
        Authority = "https://identity.example.com",
        ClientId = "example",
        ClientSecret = "private-client-secret",
    };

    private static OidcProviderConfiguration DisabledProvider(Guid id) => new()
    {
        ProviderId = id,
        Name = "Example",
        ProviderType = "Generic",
        Authority = "https://identity.example.com",
        ClientId = "example",
        ClientSecret = "private-client-secret",
        Enabled = false
    };
}
