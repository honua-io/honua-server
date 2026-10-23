// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.ControlPlane;
using Honua.Core.Features.Capabilities;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.FileImport.Abstractions;
using Honua.Server.Features.AgentWorkspace;
using Honua.Server.Features.Import;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.AgentWorkspace;

[Collection(RedisFixture.CollectionName)]
[Protocol(TestProtocols.Infrastructure)]
[Operation(Operations.TestInfrastructure)]
public sealed class LocalDatasetImportCompositionTests(RedisFixture redis)
{
    [UnitTheory]
    [InlineData(true, true, true, false, true)]
    [InlineData(false, true, true, false, false)]
    [InlineData(true, false, true, false, false)]
    [InlineData(true, true, false, false, false)]
    [InlineData(true, true, true, true, false)]
    public void LocalJobStoreRequiresExplicitProfileAndNeverMintsPaidEntitlement(
        bool enabled, bool deployment, bool redis, bool frozen, bool expected)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AgentWorkspace:DatasetImport:LocalStorage:Enabled"] = enabled.ToString(),
            ["AgentWorkspace:DeploymentId"] = deployment ? "local-test" : null,
            ["AWS_LAMBDA_FUNCTION_NAME"] = frozen ? "api" : null
        }).Build();
        var services = new ServiceCollection();
        if (redis) services.AddSingleton(Substitute.For<IConnectionMultiplexer>());
        services.AddDatasetImportLocalJobSubstrate(configuration);
        services.AddDatasetImportLocalJobSubstrate(configuration);
        Assert.Equal(expected ? 1 : 0, services.Count(descriptor => descriptor.ServiceType == typeof(IExecutionJobStore)));
        Assert.DoesNotContain(services, descriptor => descriptor.ServiceType == typeof(DurableJobSubstrateEntitlement));
    }

    [UnitTheory]
    [InlineData(true, true, true, true, false, false, "DurableLocal")]
    [InlineData(false, true, true, true, false, false, "InProcess")]
    [InlineData(true, false, true, true, false, false, "InProcess")]
    [InlineData(true, true, false, true, false, false, "InProcess")]
    [InlineData(true, true, true, false, false, false, "InProcess")]
    [InlineData(true, true, true, true, true, false, "InProcess")]
    [InlineData(true, true, true, true, false, true, "Unavailable")]
    public void CommunityLocalImportsRequireComposedInfrastructureAndCannotEnableLambdaExecution(
        bool localStorage, bool hasStore, bool hasQueue, bool redisConfigured, bool unreachable,
        bool frozen, string expected)
    {
        var collection = new ServiceCollection();
        collection.Configure<DurableJobSubstrateOptions>(options =>
        {
            options.RedisConfigured = redisConfigured;
            options.RedisEntitled = false;
            if (unreachable) options.RedisDurabilityFailure = DurableJobSubstrateCause.RedisUnreachable;
        });
        collection.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(
            frozen ? new Dictionary<string, string?> { ["AWS_LAMBDA_FUNCTION_NAME"] = "test-api" } : []).Build());
        if (localStorage) collection.AddSingleton<DatasetImportLocalStorage>();
        if (hasStore) collection.AddSingleton(Substitute.For<IExecutionJobStore>());
        if (hasQueue) collection.AddSingleton(Substitute.For<IJobQueue>());
        using var provider = collection.BuildServiceProvider();
        Assert.Equal(expected, BackgroundImportRunnerDecision.Resolve(provider).Mode.ToString());
        // Local imports do not promote the general paid job-substrate capability.
        Assert.NotEqual(DurableJobSubstrateCause.Available,
            provider.GetRequiredService<IOptions<DurableJobSubstrateOptions>>().Value.Classify(hasStore, hasQueue));
    }

    [UnitTest]
    public async Task ExecutionRegistrationWithoutHostDependenciesDoesNotAdvertiseReadiness()
    {
        var collection = new ServiceCollection();
        collection.AddDatasetImportLocalExecution();
        using var provider = collection.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<IDatasetImportSubmissionReadiness>().IsReadyAsync());
    }

    [IntegrationTest]
    public async Task RealLocalHostRequiresReceiptStorageAndRunningWorker()
    {
        if (!OperatingSystem.IsLinux()) return;
        var postgres = new PostgresFixture();
        await postgres.InitializeAsync();
        string? database = null;
        var root = Path.Join(Path.GetTempPath(), "honua-local-import-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var connectionString = await postgres.CreateIsolatedDatabaseAsync(nameof(LocalDatasetImportCompositionTests));
            database = new NpgsqlConnectionStringBuilder(connectionString).Database;
            var settings = new Dictionary<string, string?>
            {
                ["HONUA_REGISTER_TEST_INFRASTRUCTURE"] = "true",
                ["HONUA_SKIP_MIGRATIONS"] = "false",
                ["HONUA_DEV_AUTH"] = "false",
                ["HONUA_DEV_AUTH_ALLOW_BYPASS"] = "false",
                ["HONUA_ADMIN_PASSWORD"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ["ConnectionStrings:DefaultConnection"] = connectionString,
                ["ConnectionStrings:honua"] = connectionString,
                ["ConnectionStrings:redis"] = redis.ConnectionString,
                ["DataSource:Provider"] = "PostGIS",
                ["Licensing:DevGrantEdition"] = "Community",
                ["Database:Schema"] = "honua",
                ["AgentWorkspace:DeploymentId"] = "local-import-" + Guid.NewGuid().ToString("N"),
                ["Security:ConnectionEncryption:MasterKey"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)),
                ["Security:ConnectionEncryption:Salt"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["Observability:OpsHealthRollup:Enabled"] = "false",
                ["FileStorage:Provider"] = "Local",
                ["FileStorage:LocalStorage:BasePath"] = Path.Join(root, "general"),
                ["AgentWorkspace:DatasetImport:LocalStorage:Enabled"] = "true",
                ["AgentWorkspace:DatasetImport:LocalStorage:RootDirectory"] = Path.Join(root, "imports"),
                ["AgentWorkspace:DatasetImport:LocalStorage:DomainId"] = Guid.NewGuid().ToString("N"),
                ["AgentWorkspace:DatasetImport:LocalStorage:GlobalBytes"] = "67108864",
                ["AgentWorkspace:DatasetImport:LocalStorage:TenantBytes"] = "67108864",
                ["AgentWorkspace:DatasetImport:LocalStorage:OwnerBytes"] = "67108864",
                ["AgentWorkspace:DatasetImport:LocalStorage:MaximumObjects"] = "16",
                ["AgentWorkspace:DatasetImport:LocalStorage:JournalCapacity"] = "16",
                ["AgentWorkspace:DatasetImport:LocalStorage:MaximumScratchBytes"] = "16777216",
                ["AgentWorkspace:DatasetImport:LocalStorage:MaximumFileBytes"] = "16777216"
            };
            // Use the production composition and migrations. WebAppFixture's
            // provider replacements would hide registration-order regressions.
            await using var factory = ConfiguredWebApplicationFactory.Create(builder =>
            {
                foreach (var setting in settings) builder.UseSetting(setting.Key, setting.Value);
            });
            await using var scope = factory.Services.CreateAsyncScope();
            Assert.Equal(BackgroundImportRunnerMode.DurableLocal,
                scope.ServiceProvider.GetRequiredService<BackgroundImportRunnerDecision>().Mode);
            Assert.IsType<DurableImportJobService>(scope.ServiceProvider.GetRequiredService<IImportJobService>());
            _ = scope.ServiceProvider.GetRequiredService<IFileImportService>();
            var worker = Assert.Single(factory.Services.GetServices<IHostedService>().OfType<JobExecutionService>());
            var readiness = scope.ServiceProvider.GetRequiredService<IDatasetImportSubmissionReadiness>();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ready = false;
            while (!deadline.IsCancellationRequested)
            {
                if (await readiness.IsReadyAsync()) { ready = true; break; }
                await Task.Delay(50);
            }
            var diagnostic = (LocalDatasetImportSubmissionReadiness)readiness;
            Assert.True(ready, diagnostic.LastFailure + Environment.NewLine + diagnostic.LastException);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var hideReceipt = connection.CreateCommand())
            {
                hideReceipt.CommandText = "ALTER TABLE honua.import_commit_receipt RENAME TO unavailable_import_commit_receipt";
                await hideReceipt.ExecuteNonQueryAsync();
            }
            Assert.False(await readiness.IsReadyAsync());
            await using (var restoreReceipt = connection.CreateCommand())
            {
                restoreReceipt.CommandText = "ALTER TABLE honua.unavailable_import_commit_receipt RENAME TO import_commit_receipt";
                await restoreReceipt.ExecuteNonQueryAsync();
            }
            Assert.True(await readiness.IsReadyAsync());
            using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(stopDeadline.Token);
            Assert.False(await readiness.IsReadyAsync());
        }
        finally
        {
            if (database is not null) await postgres.DropDatabaseAsync(database);
            await postgres.DisposeAsync();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
