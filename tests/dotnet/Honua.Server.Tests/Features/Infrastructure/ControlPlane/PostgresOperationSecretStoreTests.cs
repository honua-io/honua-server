// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresOperationSecretStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private const string InstanceId = "opi-1";
    private const string OperationId = "admin.connection.create";
    private const string PrincipalId = "operator";
    private const string TenantId = "public";
    private const string SecretValue = "correct horse battery staple";

    private readonly IDataProtectionProvider _keyRing = new EphemeralDataProtectionProvider();

    [IntegrationTest]
    public async Task Store_PersistsOnlyAProtectedEnvelope()
    {
        await using var harness = await CreateHarnessAsync();

        var reference = Store(harness);

        var stored = await harness.ScalarAsync<string>(
            $"SELECT protected_value FROM {harness.Table("operation_secrets")} WHERE reference_id = @id",
            new NpgsqlParameter("id", reference.ReferenceId));
        stored.Should().NotBeNullOrEmpty().And.NotContain(SecretValue);
    }

    [IntegrationTest]
    public async Task Consume_SixteenConcurrentCallers_ExactlyOneReceivesTheValue()
    {
        await using var harness = await CreateHarnessAsync();
        var reference = Store(harness);

        var consumed = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            harness.CreateSecretStore(_keyRing).Consume(reference, new OperationSecretBinding(InstanceId, OperationId, PrincipalId, TenantId)))));

        consumed.Where(value => value is not null).Should().Equal(SecretValue);
    }

    [IntegrationTheory]
    [InlineData("opi-other", OperationId, PrincipalId, TenantId)]
    [InlineData(InstanceId, "admin.other.operation", PrincipalId, TenantId)]
    [InlineData(InstanceId, OperationId, "another-operator", TenantId)]
    [InlineData(InstanceId, OperationId, PrincipalId, "another-tenant")]
    public async Task Consume_WrongBinding_ReturnsNullAndKeepsTheSecret(
        string instanceId,
        string operationId,
        string principalId,
        string tenantId)
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateSecretStore(_keyRing);
        var reference = Store(harness);

        store.Consume(reference, new OperationSecretBinding(instanceId, operationId, principalId, tenantId)).Should().BeNull();

        store.Consume(reference, new OperationSecretBinding(InstanceId, OperationId, PrincipalId, TenantId)).Should().Be(SecretValue);
    }

    [IntegrationTest]
    public async Task Consume_AfterExpiry_ReturnsNull()
    {
        await using var harness = await CreateHarnessAsync();
        var store = harness.CreateSecretStore(_keyRing);
        var reference = Store(harness);
        await harness.ExecuteAsync($"UPDATE {harness.Table("operation_secrets")} SET expires_at = now() - interval '1 second'");

        store.Consume(reference, new OperationSecretBinding(InstanceId, OperationId, PrincipalId, TenantId)).Should().BeNull();

        (await store.DeleteExpiredAsync(batchSize: 100, CancellationToken.None)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task Consume_WithADifferentKeyRing_ReturnsNullAndSpendsTheSecret()
    {
        await using var harness = await CreateHarnessAsync();
        var reference = Store(harness);

        harness.CreateSecretStore(new EphemeralDataProtectionProvider())
            .Consume(reference, new OperationSecretBinding(InstanceId, OperationId, PrincipalId, TenantId)).Should().BeNull();

        harness.CreateSecretStore(_keyRing)
            .Consume(reference, new OperationSecretBinding(InstanceId, OperationId, PrincipalId, TenantId)).Should().BeNull();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresOperationSecretStoreTests));

    private OperationSecretReference Store(ControlPlaneStateHarness harness)
        => harness.CreateSecretStore(_keyRing).Store(new OperationSecretBinding(InstanceId, OperationId, PrincipalId, TenantId), "password", SecretValue);
}
