// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.EmbedGovernance.Domain;
using Honua.Server.Features.Admin.EmbedGovernance;
using Honua.Server.Features.Console.Services;
using Honua.Server.Tests.Features.Infrastructure.ControlPlane;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Admin;

// Each store instance is one task over the same tables.
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresEmbedKeyStoreTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    private static readonly EmbedKeyScope _scope = new()
    {
        AllowedEmbedOrigins = ["https://maps.example.test"],
        RateLimitRequestsPerWindow = 100,
        RateLimitWindow = TimeSpan.FromMinutes(5),
    };

    [IntegrationTest]
    public async Task KeyCreatedOnTaskA_IsAcceptedOnTaskBAndAfterRestart()
    {
        await using var harness = await CreateHarnessAsync();
        var created = await Keys(harness).CreateAsync("Partner site", _scope, null, "admin", CancellationToken.None);

        var validated = await Keys(harness).ValidateAsync(created.Key, CancellationToken.None);

        validated!.Record.Id.Should().Be(created.Record.Id);
        validated.Record.Scope.AllowedEmbedOrigins.Should().Equal("https://maps.example.test");
        validated.Record.Scope.RateLimitWindow.Should().Be(TimeSpan.FromMinutes(5));
        (await Keys(harness).ListAsync(CancellationToken.None)).Should().ContainSingle().Which.Name.Should().Be("Partner site");
    }

    [IntegrationTest]
    public async Task KeyRevokedOnTaskA_IsRefusedOnTaskB()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = Keys(harness);
        var taskB = Keys(harness);
        var created = await taskA.CreateAsync("Partner site", _scope, null, "admin", CancellationToken.None);
        (await taskB.ValidateAsync(created.Key, CancellationToken.None)).Should().NotBeNull();

        (await taskA.RevokeAsync(created.Record.Id, CancellationToken.None))!.RevokedAt.Should().NotBeNull();

        (await taskB.ValidateAsync(created.Key, CancellationToken.None)).Should().BeNull();
        (await taskB.GetAsync(created.Record.Id, CancellationToken.None))!.GetStatus(DateTimeOffset.UtcNow)
            .Should().Be(EmbedKeyStatus.Revoked);
        (await taskB.RotateAsync(created.Record.Id, CancellationToken.None)).Should().BeNull();
    }

    [IntegrationTest]
    public async Task KeyRotatedOnTaskA_RefusesTheOldSecretOnTaskB_AndAcceptsTheNewOne()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = Keys(harness);
        var taskB = Keys(harness);
        var created = await taskA.CreateAsync("Partner site", _scope, null, "admin", CancellationToken.None);
        (await taskB.ValidateAsync(created.Key, CancellationToken.None)).Should().NotBeNull();
        await taskB.RecordRequestAsync(created.Record.Id, _scope.RateLimitWindow, CancellationToken.None);

        var rotated = await taskA.RotateAsync(created.Record.Id, CancellationToken.None);

        rotated!.Key.Should().NotBe(created.Key);
        (await taskB.ValidateAsync(created.Key, CancellationToken.None)).Should().BeNull();
        (await taskB.ValidateAsync(rotated.Key, CancellationToken.None))!.Record.Id.Should().Be(created.Record.Id);
        (await taskB.GetAsync(created.Record.Id, CancellationToken.None))!.LastUsedAt.Should().BeNull();
        (await taskB.RecordRequestAsync(created.Record.Id, _scope.RateLimitWindow, CancellationToken.None)).Should().Be(1);
    }

    [IntegrationTest]
    public async Task RequestsOnTwoTasks_CountInOneWindow_AndRecordTheLastUse()
    {
        await using var harness = await CreateHarnessAsync();
        var taskA = Keys(harness);
        var taskB = Keys(harness);
        var created = await taskA.CreateAsync("Partner site", _scope, null, "admin", CancellationToken.None);

        var counts = await Task.WhenAll(Enumerable.Range(0, 12).Select(index =>
            (index % 2 == 0 ? taskA : taskB).RecordRequestAsync(created.Record.Id, _scope.RateLimitWindow, CancellationToken.None)));

        counts.Order().Should().Equal(Enumerable.Range(1, 12));
        (await taskB.ListAsync(CancellationToken.None)).Single().LastUsedAt.Should().NotBeNull();
        (await taskA.GetAsync(created.Record.Id, CancellationToken.None))!.LastUsedAt.Should().NotBeNull();
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresEmbedKeyStoreTests));

    private static PostgresEmbedKeyStore Keys(ControlPlaneStateHarness harness)
        => new(new ConsoleStateStorage(harness.CreateConfigurationDocuments(), TimeProvider.System), harness.Database);
}
