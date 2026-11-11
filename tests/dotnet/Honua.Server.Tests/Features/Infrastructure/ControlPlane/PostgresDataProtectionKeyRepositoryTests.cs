// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using FluentAssertions;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.TestInfrastructure)]
public sealed class PostgresDataProtectionKeyRepositoryTests(DatabaseFixtureAdapter database) : IClassFixture<DatabaseFixtureAdapter>
{
    [IntegrationTest]
    public async Task Protect_OnOneProvider_UnprotectsOnASecondProviderOverTheSameTable()
    {
        await using var harness = await CreateHarnessAsync();
        using var firstNode = BuildNode(harness);
        var protectedPayload = firstNode.GetDataProtector("two-node").Protect("shared secret");

        using var secondNode = BuildNode(harness);

        secondNode.GetDataProtector("two-node").Unprotect(protectedPayload).Should().Be("shared secret");
        harness.CreateKeyRepository().GetAllElements().Should().ContainSingle(element => element.Name == "key");
    }

    [IntegrationTest]
    public async Task EnsureAllElementsAreProtected_PlaintextKey_Throws()
    {
        await using var harness = await CreateHarnessAsync();
        var repository = harness.CreateKeyRepository();
        repository.StoreElement(
            XElement.Parse("<key id=\"plain\"><descriptor><descriptor><masterKey><value>AAAA</value></masterKey></descriptor></descriptor></key>"),
            "plain");

        var check = repository.EnsureAllElementsAreProtected;

        check.Should().Throw<InvalidOperationException>().WithMessage("*unprotected key 'plain'*");
    }

    [IntegrationTest]
    public async Task GetAllElements_ReturnsElementsInTheOrderTheyWereStored()
    {
        await using var harness = await CreateHarnessAsync();
        var repository = harness.CreateKeyRepository();
        repository.StoreElement(new XElement("revocation", new XAttribute("id", "first")), "first");
        repository.StoreElement(new XElement("revocation", new XAttribute("id", "second")), "second");

        repository.GetAllElements().Select(element => element.Attribute("id")!.Value).Should().Equal("first", "second");
    }

    private Task<ControlPlaneStateHarness> CreateHarnessAsync()
        => ControlPlaneStateHarness.CreateAsync(database, nameof(PostgresDataProtectionKeyRepositoryTests));

    private static ServiceProvider BuildNode(ControlPlaneStateHarness harness)
    {
        var services = new ServiceCollection();
        services.AddDataProtection().SetApplicationName("Honua.Server");
        services.Configure<KeyManagementOptions>(options => options.XmlRepository = harness.CreateKeyRepository());
        return services.BuildServiceProvider();
    }
}
