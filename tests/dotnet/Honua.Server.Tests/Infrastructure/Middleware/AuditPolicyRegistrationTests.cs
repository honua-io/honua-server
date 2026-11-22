// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using FluentAssertions;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Infrastructure.Middleware;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Middleware;

public sealed class AuditPolicyRegistrationTests
{
    [UnitTheory]
    [InlineData("true", 1)]
    [InlineData("false", 0)]
    [InlineData(null, 0)]
    public async Task ConfiguredSingleWriteSwitch_ReachesTheRegisteredFeatureWriter(string? configured, int expectedEvents)
    {
        var settings = new Dictionary<string, string?>();
        if (configured is not null)
        {
            settings["AuditLog:Policy:RecordSingleFeatureWrites"] = configured;
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var auditLog = Substitute.For<IAuditLog>();
        var inner = Substitute.For<IFeatureWriter>();
        var feature = new Feature { Id = 5, Geometry = null, Attributes = ImmutableDictionary<string, object?>.Empty };
        inner.CreateAsync(Arg.Any<int>(), Arg.Any<Feature>(), Arg.Any<CancellationToken>()).Returns(feature);

        var services = new ServiceCollection();
        services.AddSingleton(auditLog);
        services.AddSingleton(inner);
        services.AddHonuaAuditPolicy(configuration);
        services.AddAuditingFeatureWriter();
        await using var provider = services.BuildServiceProvider();

        await provider.GetRequiredService<IFeatureWriter>().CreateAsync(3, feature);

        await auditLog.Received(expectedEvents).RecordAsync(
            Arg.Is<AuditEvent>(static e => e.Action == "feature.create"),
            Arg.Any<CancellationToken>());
    }
}
