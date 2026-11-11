// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text;
using FluentAssertions;
using Honua.Infrastructure.Security;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresSecurityTokenStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly TimeSpan _lifetime = TimeSpan.FromMinutes(15);
    private static readonly byte[] _payload = Encoding.UTF8.GetBytes("{\"principal\":\"operator\"}");

    [IntegrationTest]
    public async Task FindAsync_OnASecondStore_ReturnsWhatTheFirstStored()
    {
        await using var harness = await CreateHarnessAsync();
        var key = NewKey(SecurityTokenKind.AdminSession);
        await harness.CreateSecurityTokenStore().StoreAsync(key, _payload, _lifetime, CancellationToken.None);

        var found = await harness.CreateSecurityTokenStore().FindAsync(key, CancellationToken.None);

        found!.Payload.Should().Equal(_payload);
        (await harness.CreateSecurityTokenStore().FindAsync(key with { Kind = SecurityTokenKind.PortalToken }, CancellationToken.None))
            .Should().BeNull("a value is only valid for the kind it was issued as");
    }

    [IntegrationTest]
    public async Task StoreAsync_PersistsADigestAndNeverTheBearerValue()
    {
        await using var harness = await CreateHarnessAsync();
        var key = NewKey(SecurityTokenKind.PortalToken);
        await harness.CreateSecurityTokenStore().StoreAsync(key, _payload, _lifetime, CancellationToken.None);

        var rowAsText = await harness.ScalarAsync<string>(
            $"SELECT kind || encode(token_hash, 'hex') || encode(token_hash, 'escape') FROM {harness.Table("security_tokens")}");

        rowAsText.Should().NotContain(key.Value);
    }

    [IntegrationTest]
    public async Task StoreAsync_PersistsThePayloadProtectedAndReturnsItInTheClear()
    {
        await using var harness = await CreateHarnessAsync();
        var key = NewKey(SecurityTokenKind.AdminSession);
        await harness.CreateSecurityTokenStore().StoreAsync(key, _payload, _lifetime, CancellationToken.None);

        var stored = await harness.ScalarAsync<byte[]>($"SELECT payload FROM {harness.Table("security_tokens")}");

        Encoding.UTF8.GetString(stored!).Should().NotContain("operator");
        (await harness.CreateSecurityTokenStore().FindAsync(key, CancellationToken.None))!.Payload.Should().Equal(_payload);
    }

    [IntegrationTest]
    public async Task ConsumeAsync_SixteenConcurrentCallers_ExactlyOneReceivesThePayload()
    {
        await using var harness = await CreateHarnessAsync();
        var key = NewKey(SecurityTokenKind.PortalAuthorizationCode);
        await harness.CreateSecurityTokenStore().StoreAsync(key, _payload, _lifetime, CancellationToken.None);

        var consumed = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ =>
            harness.CreateSecurityTokenStore().ConsumeAsync(key, CancellationToken.None)));

        consumed.Count(token => token is not null).Should().Be(1);
        (await harness.CreateSecurityTokenStore().FindAsync(key, CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task RevokeAsync_OnOneStore_IsEffectiveOnTheNextReadOfAnother()
    {
        await using var harness = await CreateHarnessAsync();
        var key = NewKey(SecurityTokenKind.AdminSession);
        var otherNode = harness.CreateSecurityTokenStore();
        await harness.CreateSecurityTokenStore().StoreAsync(key, _payload, _lifetime, CancellationToken.None);
        (await otherNode.FindAsync(key, CancellationToken.None)).Should().NotBeNull();

        await harness.CreateSecurityTokenStore().RevokeAsync(key, CancellationToken.None);

        (await otherNode.FindAsync(key, CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task FindAsync_AndConsumeAsync_AfterExpiry_ReturnNullBeforeAnySweep()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateSecurityTokenStore();
        var key = NewKey(SecurityTokenKind.PortalRefreshToken);
        await store.StoreAsync(key, _payload, _lifetime, CancellationToken.None);
        await harness.ExecuteAsync($"UPDATE {harness.Table("security_tokens")} SET expires_at = now() - interval '1 second'");

        (await store.FindAsync(key, CancellationToken.None)).Should().BeNull();
        (await store.ConsumeAsync(key, CancellationToken.None)).Should().BeNull();
        (await store.DeleteExpiredAsync(batchSize: 100, CancellationToken.None)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task RestoreAsync_KeepsTheOriginalExpiry()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateSecurityTokenStore();
        var key = NewKey(SecurityTokenKind.PortalRefreshToken);
        await store.StoreAsync(key, _payload, TimeSpan.FromDays(90), CancellationToken.None);
        var consumed = await store.ConsumeAsync(key, CancellationToken.None);

        await store.RestoreAsync(key, consumed!, CancellationToken.None);

        var restored = await store.FindAsync(key, CancellationToken.None);
        restored!.ExpiresAt.Should().Be(consumed!.ExpiresAt);
        restored.Payload.Should().Equal(_payload);
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresSecurityTokenStoreTests));

    private static SecurityTokenKey NewKey(SecurityTokenKind kind) => new(kind, Guid.NewGuid().ToString("N"));
}
