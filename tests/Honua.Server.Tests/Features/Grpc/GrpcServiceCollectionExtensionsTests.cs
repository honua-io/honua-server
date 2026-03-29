// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Grpc.AspNetCore.Server;
using Honua.Server.Features.Grpc;
using Honua.Server.Features.Infrastructure.Services;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Grpc;

public sealed class GrpcServiceCollectionExtensionsTests
{
    [UnitTest]
    public void AddHonuaGrpc_ConfiguresCompressionAndMessageSizes()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Grpc:MaxReceiveMessageSize"] = "2097152",
                ["Grpc:MaxSendMessageSize"] = "4194304",
                ["Grpc:ResponseCompressionAlgorithm"] = "gzip"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddHonuaGrpc(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;

        options.MaxReceiveMessageSize.Should().Be(2 * 1024 * 1024);
        options.MaxSendMessageSize.Should().Be(4 * 1024 * 1024);
        options.ResponseCompressionAlgorithm.Should().Be("gzip");
    }

    [UnitTest]
    public void AddHonuaGrpc_UsesSaneDefaults_WhenConfigMissing()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddHonuaGrpc(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;

        options.MaxReceiveMessageSize.Should().Be(16 * 1024 * 1024);
        options.MaxSendMessageSize.Should().Be(16 * 1024 * 1024);
        options.ResponseCompressionAlgorithm.Should().Be("gzip");
    }

    [UnitTest]
    public void AddHonuaGrpc_EnableDetailedErrors_ConfiguredFromConfig()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Grpc:EnableDetailedErrors"] = "true"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddHonuaGrpc(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;

        options.EnableDetailedErrors.Should().BeTrue();
    }

    [UnitTest]
    public void AddHonuaGrpc_EnableDetailedErrors_DefaultsFalse()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddHonuaGrpc(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;

        options.EnableDetailedErrors.Should().BeFalse();
    }

    [UnitTest]
    public void AddHonuaGrpc_StreamBatchSize_ConfiguredFromConfig()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Grpc:StreamBatchSize"] = "500"
            })
            .Build();
        var services = new ServiceCollection();

        services.AddHonuaGrpc(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcOptions>>().Value;

        options.StreamBatchSize.Should().Be(500);
    }

    [UnitTest]
    public void AddHonuaGrpc_StreamBatchSize_DefaultsTo1000()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddHonuaGrpc(configuration);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcOptions>>().Value;

        options.StreamBatchSize.Should().Be(1000);
    }

    [UnitTest]
    public void AddHonuaGrpc_RegistersExceptionInterceptorAndSpatialReferenceResolver()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddHonuaGrpc(configuration);

        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(GrpcExceptionInterceptor)
            && descriptor.Lifetime == ServiceLifetime.Singleton);
        services.Should().Contain(descriptor =>
            descriptor.ServiceType == typeof(SpatialReferenceResolver)
            && descriptor.Lifetime == ServiceLifetime.Scoped);

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GrpcServiceOptions>>().Value;

        options.Interceptors.Should().ContainSingle(interceptor => interceptor.Type == typeof(GrpcExceptionInterceptor));
    }
}
