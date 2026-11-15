// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;
using Honua.ControlPlane;
using Honua.Core.Features.Collaboration.FeatureLocks;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Collaboration.FeatureLocks;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static Honua.Server.Tests.Features.Protocols.Ogc.Api.Features.OgcFeaturesFeatureLockEnforcementTests;

namespace Honua.Server.Tests.Features.Protocols.Ogc.Api.Features;

// Two hosts whose PostgreSQL lease stores share one schema are two tasks of one deployment. The schema is the
// test's own: test hosts share one system schema, and durable leases there would block other classes' edits.
[Protocol(TestProtocols.OgcApiFeatures)]
[Collection("Database")]
public sealed class OgcFeaturesTwoTaskLockTests
{
    [IntegrationTest]
    [Operation(Operations.Update)]
    [Endpoint("PUT /ogc/features/collections/{collectionId}/items/{featureId}")]
    public async Task Replace_OnASecondTask_IsRefusedWhileTheFirstTaskHoldsTheLease_AndAcceptedAfterRelease()
    {
        var leaseSchema = "feature_locks_" + Guid.NewGuid().ToString("N");
        await using var firstTask = SharedLeaseHost(leaseSchema);
        await firstTask.InitializeAsync();
        firstTask.EnableV2ServiceEditingCapabilities(ServiceName, ["Create", "Update", "Delete"]);
        await CreateLeaseTableAsync(firstTask.Postgres, leaseSchema);
        try
        {
            await using var secondTask = SharedLeaseHost(leaseSchema).ShareSchemaWith(firstTask);
            await secondTask.InitializeAsync();
            secondTask.EnableV2ServiceEditingCapabilities(ServiceName, ["Create", "Update", "Delete"]);
            await AssertLeaseBindsSecondTaskAsync(firstTask, secondTask);
        }
        finally
        {
            await firstTask.Postgres.DropSchemaAsync(leaseSchema);
        }
    }

    private static async Task AssertLeaseBindsSecondTaskAsync(WebAppFixture firstTask, WebAppFixture secondTask)
    {
        var objectId = await firstTask.InsertFeatureAsync(TestLayerId, "two-task-original");
        var feature = FeatureRef.Canonical(ServiceName, TestLayerId, objectId);
        var locks = firstTask.GetService<IFeatureLockService>();
        (await locks.ClaimAsync(feature, new LockHolder(Holder), TimeSpan.FromMinutes(1), FeatureLockAccessContext.AuthorizedWrite))
            .IsSuccess.Should().BeTrue();
        var path = $"/ogc/features/collections/{TestLayerId}/items/{objectId}";

        using (var intruder = ReplacementBody(objectId, "two-task-intruder"))
        {
            (await secondTask.Client.PutAsync(path, intruder)).StatusCode.Should().Be(HttpStatusCode.Locked);
        }
        (await firstTask.ReadStoredFeatureNameAsync(TestLayerId, objectId)).Should().Be(
            "two-task-original", "a lease taken on one task binds the other");

        (await locks.ReleaseAsync(feature, new LockHolder(Holder), FeatureLockAccessContext.AuthorizedWrite)).Status
            .Should().Be(FeatureLockReleaseStatus.Released);
        using var afterRelease = ReplacementBody(objectId, "two-task-after-release");
        (await secondTask.Client.PutAsync(path, afterRelease)).IsSuccessStatusCode.Should().BeTrue();
        (await firstTask.ReadStoredFeatureNameAsync(TestLayerId, objectId)).Should().Be("two-task-after-release");
    }

    private static WebAppFixture SharedLeaseHost(string leaseSchema)
        => new WebAppFixture().WithTestLicense(HonuaEdition.Pro).ConfigureServices(services =>
        {
            services.RemoveAll<IFeatureLockService>();
            services.AddSingleton<IFeatureLockService>(provider => new PostgresFeatureLockService(
                new JobStoreDatabase(provider.GetRequiredService<NpgsqlDataSource>(), leaseSchema)));
        });

    private static async Task CreateLeaseTableAsync(PostgresFixture postgres, string leaseSchema)
    {
        await postgres.CreateSchemaUnderLockAsync(leaseSchema);
        await using var stream = typeof(PostgresFeatureLockService).Assembly.GetManifestResourceStream(
            "Honua.Server.Migrations.180_CreateFeatureLocks.sql")
            ?? throw new InvalidOperationException("Feature lock migration is missing.");
        using var reader = new StreamReader(stream);
        await postgres.ExecuteDdlUnderLockAsync((await reader.ReadToEndAsync())
            .Replace("$HonuaSchema$", new NpgsqlCommandBuilder().QuoteIdentifier(leaseSchema), StringComparison.Ordinal));
    }
}
