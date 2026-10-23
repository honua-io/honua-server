// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Admin;

/// <summary>Races managed-key writes against each other and against validation on both registries (#4571).</summary>
/// <remarks>These store-level integration tests issue no HTTP requests and claim no endpoint coverage.</remarks>
[Collection("Redis")]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.ApiKeyManagement)]
public sealed class AdminApiKeyStoreRaceTests(RedisFixture redis)
{
    private const string RegistryPrefix = "honua:auth:admin-api-key:";

    [IntegrationTheory]
    [InlineData("redis", 60)]
    [InlineData("in-memory", 2000)]
    public async Task RevokeRacingRotation_LeavesNoCredentialThatAuthenticates(string storeKind, int rounds)
    {
        await using var harness = await StoreHarness.CreateAsync(storeKind, redis);
        for (var round = 0; round < rounds; round++)
        {
            var created = await harness.CreateAsync($"race-rotate-revoke-{round}");
            var rotate = Task.Run(() => harness.Store.RotateAsync(created.Record.Id, CancellationToken.None));
            var revoke = Task.Run(() => harness.Store.RevokeAsync(created.Record.Id, CancellationToken.None));
            await Task.WhenAll(rotate, revoke);

            // Revocation was acknowledged, so whichever write reached the registry first, no
            // secret for this record may authenticate afterwards.
            Assert.NotNull((await revoke)?.RevokedAt);
            Assert.NotNull((await harness.Store.GetAsync(created.Record.Id, CancellationToken.None))?.RevokedAt);
            Assert.Null(await harness.Store.ValidateAsync(created.Key, CancellationToken.None));
            if (await rotate is { } rotated)
            {
                Assert.Null(await harness.Store.ValidateAsync(rotated.Key, CancellationToken.None));
            }
        }
    }

    [IntegrationTheory]
    [InlineData("redis", 30)]
    [InlineData("in-memory", 500)]
    public async Task RevokeRacingValidation_IsNotLostToAUsageWrite(string storeKind, int rounds)
    {
        await using var harness = await StoreHarness.CreateAsync(storeKind, redis);
        for (var round = 0; round < rounds; round++)
        {
            var created = await harness.CreateAsync($"race-validate-revoke-{round}");
            using var stop = new CancellationTokenSource();
            var inUse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var validators = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    if (await harness.Store.ValidateAsync(created.Key, CancellationToken.None) is not null)
                    {
                        inUse.TrySetResult();
                    }
                }
            })).ToArray();

            // Revoke only once usage writes are landing, so the revocation has to beat them.
            await inUse.Task.WaitAsync(TimeSpan.FromSeconds(30));
            var revoked = await harness.Store.RevokeAsync(created.Record.Id, CancellationToken.None);
            await stop.CancelAsync();
            await Task.WhenAll(validators);

            Assert.NotNull(revoked?.RevokedAt);
            Assert.NotNull((await harness.Store.GetAsync(created.Record.Id, CancellationToken.None))?.RevokedAt);
            Assert.Null(await harness.Store.ValidateAsync(created.Key, CancellationToken.None));
        }
    }

    [IntegrationTheory]
    [InlineData("redis")]
    [InlineData("in-memory")]
    public async Task ConcurrentValidationOfOneKey_NeverDeniesIt(string storeKind)
    {
        await using var harness = await StoreHarness.CreateAsync(storeKind, redis);
        var created = await harness.CreateAsync("race-concurrent-usage");

        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(() => harness.Store.ValidateAsync(created.Key, CancellationToken.None))));

        // Each request only rewrites LastUsedAt; losing that compare-and-set to a sibling
        // request is benign and must never turn a valid key into a 401.
        Assert.All(results, result => Assert.Equal(created.Record.Id, result?.Record.Id));
        Assert.NotNull((await harness.Store.GetAsync(created.Record.Id, CancellationToken.None))?.LastUsedAt);
    }

    private sealed class StoreHarness : IAsyncDisposable
    {
        private readonly ConnectionMultiplexer? _connection;
        private readonly List<Guid> _ids = [];

        private StoreHarness(IAdminApiKeyStore store, ConnectionMultiplexer? connection)
        {
            Store = store;
            _connection = connection;
        }

        public IAdminApiKeyStore Store { get; }

        public static async Task<StoreHarness> CreateAsync(string storeKind, RedisFixture redis)
        {
            if (storeKind == "in-memory")
            {
                return new StoreHarness(new InMemoryAdminApiKeyStore(), connection: null);
            }

            var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
            return new StoreHarness(new RedisAdminApiKeyStore(connection), connection);
        }

        public async Task<AdminApiKeyCreateResult> CreateAsync(string name)
        {
            var created = await Store.CreateAsync(name, ["admin:*"], DateTimeOffset.UtcNow.AddHours(1), "operator", CancellationToken.None);
            _ids.Add(created.Record.Id);
            return created;
        }

        public async ValueTask DisposeAsync()
        {
            if (_connection is null)
            {
                return;
            }

            var database = _connection.GetDatabase();
            foreach (var id in _ids)
            {
                await database.KeyDeleteAsync($"{RegistryPrefix}{id:D}");
                await database.SetRemoveAsync(RegistryPrefix + "ids", id.ToString("D"));
                await database.SetRemoveAsync(RegistryPrefix + "active-ids", id.ToString("D"));
                await database.SetRemoveAsync(RegistryPrefix + "seen-ids", id.ToString("D"));
            }

            await _connection.DisposeAsync();
        }
    }
}
