// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Tests.Infrastructure.Hosting;

public sealed class ContainerGrpcTransportConfigurationTests
{
    [Theory]
    [InlineData("Dockerfile")]
    [InlineData("docker/Dockerfile.aot")]
    public void Dockerfile_DefinesDedicatedNativeGrpcH2cEndpoint(string relativePath)
    {
        var dockerfile = ReadRepoFile(relativePath);

        Assert.Contains("Kestrel__Endpoints__Http__Url=http://+:8080", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Kestrel__Endpoints__Http__Protocols=Http1", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Kestrel__Endpoints__Grpc__Url=http://+:8081", dockerfile, StringComparison.Ordinal);
        Assert.Contains("Kestrel__Endpoints__Grpc__Protocols=Http2", dockerfile, StringComparison.Ordinal);
        Assert.Contains("EXPOSE 8080/tcp 8081/tcp", dockerfile, StringComparison.Ordinal);
        Assert.DoesNotContain("ASPNETCORE_URLS=http://+:8080", dockerfile, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeFile_MapsNativeGrpcPortToHttp2Endpoint()
    {
        var compose = ReadRepoFile("docker-compose.yml");
        var honuaService = GetComposeServiceBlock(compose, "honua", "console");

        Assert.Contains("${HONUA_HTTP_PORT:-8080}:8080\"", honuaService, StringComparison.Ordinal);
        Assert.Contains("${HONUA_GRPC_PORT:-8081}:8081\"", honuaService, StringComparison.Ordinal);
        Assert.Contains("Kestrel__Endpoints__Http__Protocols: \"Http1\"", honuaService, StringComparison.Ordinal);
        Assert.Contains("Kestrel__Endpoints__Grpc__Protocols: \"Http2\"", honuaService, StringComparison.Ordinal);
        Assert.DoesNotContain("ASPNETCORE_URLS", honuaService, StringComparison.Ordinal);
    }

    [Fact]
    public void ComposeFile_PersistsLocalFileStorageOutsideTmpfs()
    {
        var compose = ReadRepoFile("docker-compose.yml");
        var honuaService = GetComposeServiceBlock(compose, "honua", "console");

        Assert.Contains(
            "FileStorage__LocalStorage__BasePath: \"/var/lib/honua/storage\"",
            honuaService,
            StringComparison.Ordinal);
        Assert.Contains("- honua_storage:/var/lib/honua/storage", honuaService, StringComparison.Ordinal);
        Assert.DoesNotContain("honua_storage:/tmp", honuaService, StringComparison.Ordinal);
        Assert.Contains("  honua_storage:", compose, StringComparison.Ordinal);
        Assert.Contains(
            "name: \"${HONUA_STORAGE_VOLUME_NAME:-honua_storage}\"",
            compose,
            StringComparison.Ordinal);
    }

    [Fact]
    public void BackupGuide_UsesTheMountedComposeStorageVolume()
    {
        var guide = ReadRepoFile("docs/guides/deploy/backup-and-restore.md");

        Assert.Contains("HONUA_STORAGE_VOLUME_NAME", guide, StringComparison.Ordinal);
        Assert.Contains("/var/lib/honua/storage", guide, StringComparison.Ordinal);
        Assert.Contains("tar tzf", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("STORAGE_VOLUME=honua_storage", guide, StringComparison.Ordinal);
    }

    [Fact]
    public void ClientCompatComposeFile_OverridesContainerHttpPortForLaneBaseUrls()
    {
        var compose = ReadRepoFile("docker/client-compat/compose.yml");

        Assert.Contains("ASPNETCORE_URLS: http://+:5000", compose, StringComparison.Ordinal);
        Assert.Contains("Kestrel__Endpoints__Http__Url: http://+:5000", compose, StringComparison.Ordinal);
        Assert.Contains("Kestrel__Endpoints__Http__Protocols: Http1", compose, StringComparison.Ordinal);
        Assert.Contains("Kestrel__Endpoints__Grpc__Url: http://+:5001", compose, StringComparison.Ordinal);
        Assert.Contains("Kestrel__Endpoints__Grpc__Protocols: Http2", compose, StringComparison.Ordinal);
        Assert.Contains("PUBLIC_BASE_URL: http://honua:5000", compose, StringComparison.Ordinal);
        Assert.Contains("ConnectionStrings__Redis: redis:6379", compose, StringComparison.Ordinal);
        var postgresService = GetComposeServiceBlock(compose, "postgres", "redis");
        Assert.Contains("POSTGIS_GDAL_ENABLED_DRIVERS: ENABLE_ALL", postgresService, StringComparison.Ordinal);
        var openLayersService = GetComposeServiceBlock(compose, "openlayers", "cesium");
        Assert.Contains("HONUA_SERVICE_ID: browser_compat", openLayersService, StringComparison.Ordinal);
        Assert.Contains("HONUA_LAYER_ID: \"2000\"", openLayersService, StringComparison.Ordinal);
        Assert.Contains("http://localhost:5000/healthz/ready", compose, StringComparison.Ordinal);
        Assert.Contains("image: redis:7.4-alpine", compose, StringComparison.Ordinal);
    }

    private static string GetComposeServiceBlock(string compose, string serviceName, string nextServiceName)
    {
        var startMarker = $"  {serviceName}:";
        var nextMarker = $"  {nextServiceName}:";
        var start = compose.IndexOf(startMarker, StringComparison.Ordinal);
        var end = compose.IndexOf(nextMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Could not find compose service '{serviceName}'.");
        Assert.True(end > start, $"Could not find compose service following '{serviceName}'.");
        return compose[start..end];
    }

    private static string ReadRepoFile(string relativePath)
        => File.ReadAllText(Path.Combine(FindRepositoryRoot(), relativePath));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
