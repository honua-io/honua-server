// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Postgres;
using Honua.Server;
using Microsoft.AspNetCore.Mvc;
using NetArchTest.Rules;
using Xunit;

namespace Honua.Architecture.Tests;

/// <summary>
/// Enforces dependency direction and API pattern guardrails.
/// </summary>
[Trait("Category", "Architecture")]
public sealed class DependencyRulesTests
{
    [ArchitectureTest]
    public void Core_ShouldNotDependOn_ServerOrPostgres()
    {
        var coreAssembly = typeof(Honua.Core.Features.Metadata.Domain.V2.MetadataV2Resource).Assembly;

        var result = Types.InAssembly(coreAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Honua.Server", "Honua.Postgres")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Core must not depend on Server or Postgres");
    }

    [ArchitectureTest]
    public void Postgres_ShouldNotDependOn_Server()
    {
        var postgresAssembly = typeof(ServiceCollectionExtensions).Assembly;

        var result = Types.InAssembly(postgresAssembly)
            .ShouldNot()
            .HaveDependencyOnAny("Honua.Server")
            .GetResult();

        result.IsSuccessful.Should().BeTrue("Postgres must not depend on Server");
    }

    [ArchitectureTest]
    public void Server_ShouldNotUseControllers()
    {
        var serverAssembly = typeof(EndpointRegistry).Assembly;

        var controllerTypes = serverAssembly
            .GetTypes()
            .Where(type => typeof(ControllerBase).IsAssignableFrom(type) && type != typeof(ControllerBase))
            .Select(type => type.FullName)
            .OrderBy(name => name)
            .ToArray();

        controllerTypes.Should().BeEmpty("minimal APIs are required; controllers are forbidden");

        var apiControllerTypes = serverAssembly
            .GetTypes()
            .Where(type => type.GetCustomAttributes(typeof(ApiControllerAttribute), inherit: true).Length > 0)
            .Select(type => type.FullName)
            .OrderBy(name => name)
            .ToArray();

        apiControllerTypes.Should().BeEmpty("ApiController attributes are not allowed in the server layer");
    }

    [ArchitectureTest]
    public void Core_ShouldNotOwnPortableGrpcTransportSurface()
    {
        var repositoryRoot = ArchitectureTestHelpers.ResolveRepositoryRoot();
        var forbiddenDirectories = new[]
        {
            Path.Combine(repositoryRoot, "src", "Honua.Core", "Transport", "Clients"),
            Path.Combine(repositoryRoot, "src", "Honua.Core", "Transport", "Converters"),
            Path.Combine(repositoryRoot, "src", "Honua.Core", "Transport", "Proto")
        };

        forbiddenDirectories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            .Should()
            .BeEmpty("portable gRPC clients/converters belong in Honua.Sdk.* and canonical proto definitions stay in geospatial-grpc");

        var coreProject = File.ReadAllText(Path.Combine(repositoryRoot, "src", "Honua.Core", "Honua.Core.csproj"));
        coreProject.Should().NotContain("Geospatial.Grpc", "Honua.Core should not own generated gRPC bindings");
        coreProject.Should().NotContain("Grpc.Net.Client", "gRPC client behavior belongs in Honua.Sdk.Grpc");
        coreProject.Should().NotContain("Grpc.Core.Api", "server gRPC protocol adapters should depend on gRPC directly");
        coreProject.Should().NotContain("Google.Protobuf", "protobuf mapping should stay in protocol adapters or SDK packages");
    }

    [ArchitectureTest]
    public void ClassicOgcProtocols_ShouldNotDependOn_GeoServicesProtocols()
    {
        var serverAssembly = typeof(EndpointRegistry).Assembly;

        var result = Types.InAssembly(serverAssembly)
            .That()
            .ResideInNamespace("Honua.Protocols.Ogc.Classic")
            .ShouldNot()
            .HaveDependencyOn("Honua.Protocols.GeoServices")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "classic OGC protocol adapters must consume shared rendering/query capabilities, not GeoServices adapters");
    }

    [ArchitectureTest]
    public void Postgres_ShouldNotExposeNonStaticPublicTypes()
    {
        var postgresAssembly = typeof(ServiceCollectionExtensions).Assembly;

        var invalidPublicTypes = postgresAssembly
            .GetExportedTypes()
            .Where(type => !IsStatic(type))
            .Select(type => type.FullName)
            .OrderBy(name => name)
            .ToArray();

        invalidPublicTypes.Should().BeEmpty("Postgres infrastructure types must remain internal");
    }

    private static bool IsStatic(Type type) => type.IsAbstract && type.IsSealed;
}
