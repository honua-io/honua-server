// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Import.FileImport.Resumable;
using Honua.Server.Features.AgentWorkspace;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.AgentWorkspace;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class DatasetImportLocalStorageTests(RedisFixture redis)
{
    [UnitTest]
    public void UnconfiguredHostDoesNotRegisterTransferOrClaimImportAcceptance()
    {
        var collection = new ServiceCollection();
        collection.AddDatasetImportLocalStorage(new ConfigurationBuilder().Build());
        using var services = collection.BuildServiceProvider();
        Assert.Null(services.GetService<ImportStagedSourceStorage>());
        Assert.Null(services.GetService<ImportUploadCoordinator>());
        Assert.Null(services.GetService<IDatasetImportSubmissionReadiness>());
    }

    [IntegrationTest]
    public async Task HostProfileSharesOneAccountedDomainAndRecoversWithoutReplacingGeneralStorage()
    {
        if (!OperatingSystem.IsLinux()) return;
        await using var connection = await ConnectionMultiplexer.ConnectAsync(redis.ConnectionString);
        var root = Path.Join(Path.GetTempPath(), "honua-import-profile-" + Guid.NewGuid().ToString("N"));
        var domainId = Guid.NewGuid().ToString("N");
        const string prefix = "AgentWorkspace:DatasetImport:LocalStorage:";
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [prefix + "Enabled"] = "true",
            [prefix + "RootDirectory"] = root,
            [prefix + "DomainId"] = domainId,
            [prefix + "GlobalBytes"] = "67108864",
            [prefix + "TenantBytes"] = "67108864",
            [prefix + "OwnerBytes"] = "67108864",
            [prefix + "MaximumObjects"] = "16",
            [prefix + "JournalCapacity"] = "16",
            [prefix + "MaximumScratchBytes"] = "16777216",
            [prefix + "MaximumFileBytes"] = "16777216"
        }).Build();
        var generalStorage = Substitute.For<ICloudFileStorage>();
        var collection = new ServiceCollection().AddLogging()
            .AddSingleton<IConnectionMultiplexer>(connection).AddSingleton(generalStorage)
            .AddSingleton(Substitute.For<IUploadProgressStore>())
            .AddSingleton(Substitute.For<IDatasetImportTaskProvider>());
        collection.AddDatasetImportLocalStorage(configuration);
        collection.AddSingleton(Substitute.For<IDatasetImportContributorAuthorizer>());
        using var services = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        var profile = services.GetRequiredService<DatasetImportLocalStorage>();
        var ledger = new RedisImportStorageBudget(connection, domainId, new(67108864, 67108864, 67108864, 16), Guid.NewGuid().ToString("N"));
        try
        {
            Assert.False(await profile.IsReadyAsync(default));
            Assert.Same(profile, Assert.Single(services.GetServices<IHostedService>()));
            Assert.Same(generalStorage, services.GetRequiredService<ICloudFileStorage>());
            Assert.Same(profile.Source, services.GetRequiredService<ImportStagedSourceStorage>());
            Assert.Same(profile.Scratch, services.GetRequiredService<ImportScratchStorage>());
            Assert.Same(profile.Processing, services.GetRequiredService<IImportProcessingScratchFactory>());
            Assert.True(profile.Storage.IsBoundToMutationFence(profile.Domain));
            Assert.True(profile.Scratch.IsBoundToMutationFence(profile.Domain));
            Assert.True(profile.Processing.IsBoundToMutationFence(profile.Domain));
            using var scope = services.CreateScope();
            var readiness = scope.ServiceProvider.GetRequiredService<IDatasetImportSubmissionReadiness>();
            Assert.False(await readiness.IsReadyAsync());
            Assert.True(scope.ServiceProvider.GetRequiredService<ImportUploadCoordinator>().IsBoundToStorage(profile.Source));
            await profile.StartAsync(default);
            Assert.True(await profile.IsReadyAsync(default));
            Assert.False(await readiness.IsReadyAsync(), "Initialized storage alone cannot certify execution readiness.");

            var session = new ImportUploadSession
            {
                Id = Guid.NewGuid().ToString("N"),
                Scope = new("actor", "tenant", "target", "workspace", "task", 1),
                IntentSha256 = new string('a', 64),
                FileName = "source.geojson",
                TotalBytes = 16,
                ChunkBytes = 65536,
                ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
            };
            var bytes = new byte[16];
            using var input = new MemoryStream(bytes);
            var chunk = await new ImportUploadChunkStorage(profile.Source.Storage, profile.Domain).StageAsync(session, input,
                Convert.ToHexStringLower(SHA256.HashData(bytes)));
            Assert.NotNull(await profile.Source.Storage.GetMetadataAsync(chunk.FileId));
            Assert.Empty(generalStorage.ReceivedCalls());
            Assert.Equal(16, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "bytes"));
            var journalId = (string?)await connection.GetDatabase().HashGetAsync(ledger.Key, "journal");
            Assert.False(string.IsNullOrWhiteSpace(journalId));

            await connection.GetDatabase().KeyDeleteAsync(ledger.Key);
            Assert.False(await profile.IsReadyAsync(default));
            await profile.StartAsync(default);
            Assert.True(await profile.IsReadyAsync(default));
            Assert.Equal(journalId, (string?)await connection.GetDatabase().HashGetAsync(ledger.Key, "journal"));
            Assert.Equal(16, (long)await connection.GetDatabase().HashGetAsync(ledger.Key, "bytes"));
            await profile.StopAsync(default);
            Assert.False(await profile.IsReadyAsync(default));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            await connection.GetDatabase().KeyDeleteAsync(ledger.Key);
        }
    }
}
