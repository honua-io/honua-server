// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using FluentAssertions;
using Honua.Core.Features.Identity.Domain;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresConfigurationHistoryTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task WriteAsync_CreateUpdateDelete_KeepsEncryptedHistoryAtEachGeneration()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        var store = harness.CreateOidcStore();
        var provider = Provider();
        await store.CreateProviderAsync(provider);
        await store.UpdateProviderAsync(provider.ProviderId, _ => Provider(provider.ProviderId, "changed-client"));
        await store.DeleteProviderAsync(provider.ProviderId);

        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("configuration_document_history")}"))
            .Should().Be(3);
        (await harness.ScalarAsync<long>($"SELECT max(generation) FROM {harness.Table("configuration_document_history")}"))
            .Should().Be(await harness.CreateConfigurationDocuments().GetGenerationAsync(default));
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("configuration_document_history")} WHERE payload IS NULL"))
            .Should().Be(1);
        (await harness.ScalarAsync<long>($"SELECT count(*) FROM {harness.Table("configuration_document_history")} WHERE position(convert_to('private-client-secret', 'UTF8') in payload) > 0"))
            .Should().Be(0);
    }

    private static readonly ConfigurationCollection<OidcProviderConfiguration> Collection =
        new("oidc-provider", SharedConfigurationJsonContext.Default.OidcProviderConfiguration);

    [IntegrationTest]
    public async Task RestoreAsync_PreviousValue_CreatesNewRevisionVisibleToAnotherNode()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        var documents = harness.CreateConfigurationDocuments();
        var provider = Provider();
        var key = provider.ProviderId.ToString("D");
        await harness.CreateOidcStore().CreateProviderAsync(provider);
        var target = await documents.GetGenerationAsync(default);
        await harness.CreateOidcStore().UpdateProviderAsync(provider.ProviderId, _ => Provider(provider.ProviderId, "changed-client"));
        var current = await documents.GetGenerationAsync(default);

        (await documents.RestoreAsync(Collection, new(key, target, current), default)).Should().BeTrue();

        (await harness.CreateOidcStore().GetProviderAsync(provider.ProviderId))!.ClientId.Should().Be(provider.ClientId);
        var snapshot = await harness.CreateConfigurationDocuments().ReadAsync(Collection, default);
        snapshot.Generation.Should().Be(current + 1);
        snapshot.Documents.Single().Revision.Should().Be(3);
        var history = await documents.ReadHistoryAsync(new(Collection.Kind, key), default);
        history.Select(entry => entry.Generation).Should().Equal(current + 1, current, target);
    }

    [IntegrationTest]
    public async Task RestoreAsync_ConcurrentRequests_OnlyOneWins()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        var provider = Provider();
        var store = harness.CreateOidcStore();
        var documents = harness.CreateConfigurationDocuments();
        await store.CreateProviderAsync(provider);
        var target = await documents.GetGenerationAsync(default);
        await store.DeleteProviderAsync(provider.ProviderId);
        var expected = await documents.GetGenerationAsync(default);
        var request = new ConfigurationRestore(provider.ProviderId.ToString("D"), target, expected);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            harness.CreateConfigurationDocuments().RestoreAsync(Collection, request, default)));

        results.Count(restored => restored).Should().Be(1);
        (await store.GetProviderAsync(provider.ProviderId)).Should().NotBeNull();
        (await documents.GetGenerationAsync(default)).Should().Be(expected + 1);
    }

    [IntegrationTest]
    public async Task RestoreAsync_Deletion_RestoresAbsenceAndCanBeUndone()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        var store = harness.CreateOidcStore();
        var provider = Provider();
        var documents = harness.CreateConfigurationDocuments();
        var key = provider.ProviderId.ToString("D");
        await store.CreateProviderAsync(provider);
        await store.DeleteProviderAsync(provider.ProviderId);
        var deletion = await documents.GetGenerationAsync(default);
        await store.CreateProviderAsync(provider);
        var recreated = await documents.GetGenerationAsync(default);

        (await documents.RestoreAsync(Collection, new(key, deletion, recreated), default)).Should().BeTrue();
        (await store.GetProviderAsync(provider.ProviderId)).Should().BeNull();
        (await documents.RestoreAsync(Collection, new(key, recreated, recreated + 1), default)).Should().BeTrue();
        (await store.GetProviderAsync(provider.ProviderId)).Should().NotBeNull();
        (await documents.ReadHistoryAsync(new(Collection.Kind, key), default)).Should().HaveCount(5);
    }

    [IntegrationTest]
    public async Task RestoreAsync_StaleOrWrongDocument_DoesNotChangeState()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        var documents = harness.CreateConfigurationDocuments();
        var provider = Provider();
        await harness.CreateOidcStore().CreateProviderAsync(provider);
        var generation = await documents.GetGenerationAsync(default);
        var key = provider.ProviderId.ToString("D");

        (await documents.RestoreAsync(Collection, new(key, generation, generation - 1), default)).Should().BeFalse();
        (await documents.RestoreAsync(Collection, new("other", generation, generation), default)).Should().BeFalse();
        var otherKind = Collection with { Kind = "other" };
        (await documents.RestoreAsync(otherKind, new(key, generation, generation), default)).Should().BeFalse();
        (await documents.GetGenerationAsync(default)).Should().Be(generation);
        (await documents.ReadHistoryAsync(new(Collection.Kind, key), default)).Should().ContainSingle();
    }

    [IntegrationTest]
    public async Task History_InvalidBoundsFailAndZeroGenerationDoesNotMutateState()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        var documents = harness.CreateConfigurationDocuments();
        var initial = await documents.GetGenerationAsync(default);
        var emptyPage = () => documents.ReadHistoryAsync(new(Collection.Kind, "missing", Limit: 0), default);
        var oversizedPage = () => documents.ReadHistoryAsync(new(Collection.Kind, "missing", Limit: 101), default);
        var negativeTarget = () => documents.RestoreAsync(Collection, new("missing", -1, initial), default);
        var negativeExpected = () => documents.RestoreAsync(Collection, new("missing", 0, -1), default);

        await emptyPage.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await oversizedPage.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await negativeTarget.Should().ThrowAsync<ArgumentOutOfRangeException>();
        await negativeExpected.Should().ThrowAsync<ArgumentOutOfRangeException>();
        (await documents.RestoreAsync(Collection, new("missing", 0, initial), default)).Should().BeFalse();
        (await documents.GetGenerationAsync(default)).Should().Be(initial);
    }

    [IntegrationTest]
    public async Task WriteBatchAsync_LaterConflict_RollsBackHistoryAndGeneration()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        var documents = harness.CreateConfigurationDocuments();
        var provider = Provider();
        var write = documents.PrepareWrite(Collection, new(provider.ProviderId.ToString("D"), 0, provider));
        var initial = await documents.GetGenerationAsync(default);

        (await documents.WriteBatchAsync(initial, [write, write], default)).Should().BeFalse();

        (await documents.GetGenerationAsync(default)).Should().Be(initial);
        (await harness.CreateOidcStore().ListProvidersAsync()).Should().BeEmpty();
        (await documents.ReadHistoryAsync(new(Collection.Kind, write.Key), default)).Should().BeEmpty();
    }

    [IntegrationTest]
    public async Task ReadHistoryAsync_Pages_AreBoundedAndExcludeOtherDocuments()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        var store = harness.CreateOidcStore();
        var provider = Provider();
        var key = provider.ProviderId.ToString("D");
        await store.CreateProviderAsync(provider);
        await store.UpdateProviderAsync(provider.ProviderId, _ => Provider(provider.ProviderId, "changed-client"));
        await store.DeleteProviderAsync(provider.ProviderId);
        await store.CreateProviderAsync(Provider());
        var documents = harness.CreateConfigurationDocuments();

        var page = await documents.ReadHistoryAsync(new(Collection.Kind, key, Limit: 2), default);
        page.Should().HaveCount(2);
        page[0].Deleted.Should().BeTrue();
        page[1].Deleted.Should().BeFalse();
        var next = await documents.ReadHistoryAsync(new(Collection.Kind, key, 2) { BeforeGeneration = page[^1].Generation }, default);
        next.Should().ContainSingle();
        next[0].Generation.Should().BeLessThan(page[^1].Generation);
    }

    [IntegrationTest]
    public async Task RestoreAsync_UnreadableHistory_FailsWithoutWriting()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        var provider = Provider();
        await harness.CreateOidcStore().CreateProviderAsync(provider);
        var generation = await harness.CreateConfigurationDocuments().GetGenerationAsync(default);
        var foreign = harness.CreateConfigurationDocuments(new EphemeralDataProtectionProvider());

        var restore = () => foreign.RestoreAsync(Collection, new(provider.ProviderId.ToString("D"), generation, generation), default);

        await restore.Should().ThrowAsync<CryptographicException>();
        (await foreign.GetGenerationAsync(default)).Should().Be(generation);
        (await harness.CreateOidcStore().GetProviderAsync(provider.ProviderId))!.ClientSecret.Should().Be("private-client-secret");
    }

    [IntegrationTest]
    public async Task HistoryMigration_ExistingDocuments_SeedsBaselineAndIsRepeatable()
    {
        await using var harness = await ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresConfigurationHistoryTests));
        await harness.ReplayConfigurationMigrationAsync();
        var provider = Provider();
        await harness.CreateOidcStore().CreateProviderAsync(provider);
        var documents = harness.CreateConfigurationDocuments();
        var generation = await documents.GetGenerationAsync(default);

        await harness.ReplayConfigurationHistoryMigrationAsync();
        await harness.ReplayConfigurationHistoryMigrationAsync();

        var history = await documents.ReadHistoryAsync(new(Collection.Kind, provider.ProviderId.ToString("D")), default);
        history.Should().ContainSingle().Which.Generation.Should().Be(generation);
        await harness.CreateOidcStore().DeleteProviderAsync(provider.ProviderId);
        (await documents.RestoreAsync(Collection, new(provider.ProviderId.ToString("D"), generation, generation + 1), default))
            .Should().BeTrue();
        (await harness.CreateOidcStore().GetProviderAsync(provider.ProviderId))!.ClientSecret.Should().Be("private-client-secret");
    }

    private static OidcProviderConfiguration Provider(Guid? id = null, string clientId = "example") => new()
    {
        ProviderId = id ?? Guid.NewGuid(),
        Name = "Example",
        ProviderType = "Generic",
        Authority = "https://identity.example",
        ClientId = clientId,
        ClientSecret = "private-client-secret"
    };
}
