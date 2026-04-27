// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Shared MapServer fixture for integration tests that require a live MapServer instance.
/// Uses Testcontainers to manage the MapServer container lifecycle.
/// </summary>
public sealed class MapServerFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static IContainer? _sharedContainer;
    private static string? _sharedEndpointUrl;
    private static string? _sharedStagingDirectory;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;

    private const string MapServerImage = "camptocamp/mapserver:8.0";
    private const int MapServerPort = 80;
    private const string ContainerMapFilePath = "/etc/mapserver/mapserver.map";

    public const string LayerName = "tsunami_zones";

    /// <summary>
    /// Gets the MapServer OGC endpoint URL with the fixture mapfile parameter applied.
    /// </summary>
    public string EndpointUrl => _sharedEndpointUrl ?? throw new InvalidOperationException("MapServer fixture not initialized.");

    public async Task InitializeAsync()
    {
        await _sharedLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_sharedInitialized)
            {
                await StartContainerAsync().ConfigureAwait(false);
                _sharedInitialized = true;
            }

            _sharedRefCount++;
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    public async Task DisposeAsync()
    {
        await _sharedLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_sharedRefCount > 0)
            {
                _sharedRefCount--;
            }

            if (_sharedRefCount == 0 && _sharedInitialized)
            {
                await ResetSharedStateAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _sharedLock.Release();
        }
    }

    private static async Task StartContainerAsync()
    {
        var stagingDirectory = CreateStagingDirectory();
        _sharedStagingDirectory = stagingDirectory.RootDirectory;

        try
        {
            _sharedContainer = new ContainerBuilder()
                .WithImage(MapServerImage)
                .WithPortBinding(MapServerPort, true)
                .WithResourceMapping(new DirectoryInfo(stagingDirectory.DataDirectory), "/etc/mapserver/data")
                .WithResourceMapping(new FileInfo(stagingDirectory.MapFilePath), "/etc/mapserver/")
                .Build();

            await _sharedContainer.StartAsync().ConfigureAwait(false);

            var port = _sharedContainer.GetMappedPublicPort(MapServerPort);
            _sharedEndpointUrl = $"http://127.0.0.1:{port}/?map={Uri.EscapeDataString(ContainerMapFilePath)}";

            await WaitForMapServerReadyAsync(_sharedEndpointUrl).ConfigureAwait(false);
        }
        catch
        {
            await ResetSharedStateAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ResetSharedStateAsync()
    {
        if (_sharedContainer is not null)
        {
            await _sharedContainer.DisposeAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(_sharedStagingDirectory) && Directory.Exists(_sharedStagingDirectory))
        {
            Directory.Delete(_sharedStagingDirectory, recursive: true);
        }

        _sharedContainer = null;
        _sharedEndpointUrl = null;
        _sharedStagingDirectory = null;
        _sharedInitialized = false;
    }

    private static MapServerStagingDirectory CreateStagingDirectory()
    {
        var rootDirectory = Path.Combine(Path.GetTempPath(), $"honua-mapserver-{Guid.NewGuid():N}");
        var dataDirectory = Path.Combine(rootDirectory, "data");
        Directory.CreateDirectory(dataDirectory);

        ZipFile.ExtractToDirectory(
            ReferenceServerTestData.ResolveTsunamiEvacuationZonesZipPath(),
            dataDirectory,
            overwriteFiles: true);

        File.WriteAllText(Path.Combine(dataDirectory, "featureinfo.html"), "zone=[zone_type]\nisland=[island]\n");

        var mapFilePath = Path.Combine(rootDirectory, "mapserver.map");
        File.WriteAllText(mapFilePath, MapFileContent);

        return new MapServerStagingDirectory(rootDirectory, dataDirectory, mapFilePath);
    }

    private static async Task WaitForMapServerReadyAsync(string endpointUrl)
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(4);
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        Exception? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                using var response = await httpClient.GetAsync(
                    $"{endpointUrl}&SERVICE=WMS&REQUEST=GetCapabilities&VERSION=1.3.0").ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (response.IsSuccessStatusCode &&
                    body.Contains("WMS_Capabilities", StringComparison.OrdinalIgnoreCase) &&
                    body.Contains(LayerName, StringComparison.Ordinal))
                {
                    return;
                }

                lastError = new InvalidOperationException(
                    $"MapServer readiness returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            catch (Exception ex) when (IsTransientStartupFailure(ex))
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        var errorDetails = lastError is null ? string.Empty : $" Last error: {lastError.Message}";
        throw new TimeoutException($"Timed out waiting for MapServer to become ready.{errorDetails}");
    }

    private static bool IsTransientStartupFailure(Exception exception)
        => exception is HttpRequestException or TaskCanceledException or TimeoutException;

    private sealed record MapServerStagingDirectory(string RootDirectory, string DataDirectory, string MapFilePath);

    private const string MapFileContent = """
        MAP
          NAME "honua_consume_test"
          STATUS ON
          SIZE 256 256
          EXTENT 371473.9076 2097845.7333 936390.2754 2458527.7304
          UNITS METERS
          SHAPEPATH "/etc/mapserver/data"
          IMAGETYPE "png"

          PROJECTION
            "init=epsg:3750"
          END

          OUTPUTFORMAT
            NAME "png"
            DRIVER AGG/PNG
            MIMETYPE "image/png"
            IMAGEMODE RGBA
            EXTENSION "png"
          END

          WEB
            METADATA
              "ows_title" "Honua Consume Test"
              "ows_onlineresource" "http://localhost/?map=/etc/mapserver/mapserver.map&"
              "ows_srs" "EPSG:3750 EPSG:4326"
              "ows_enable_request" "*"
              "wms_enable_request" "*"
              "wms_srs" "EPSG:3750 EPSG:4326"
              "wfs_enable_request" "*"
              "wfs_srs" "EPSG:3750 EPSG:4326"
            END
          END

          LAYER
            NAME "tsunami_zones"
            TYPE POLYGON
            STATUS ON
            DATA "Extreme_Tsunami_Evacuation_Zones"
            TEMPLATE "featureinfo.html"

            PROJECTION
              "init=epsg:3750"
            END

            CLASS
              STYLE
                COLOR 243 111 33
                OUTLINECOLOR 31 41 55
              END
            END

            METADATA
              "ows_title" "Tsunami Evacuation Zones"
              "wms_title" "Tsunami Evacuation Zones"
              "wms_srs" "EPSG:3750 EPSG:4326"
              "wms_enable_request" "*"
              "wms_queryable" "1"
              "wms_include_items" "all"
              "wfs_title" "Tsunami Evacuation Zones"
              "wfs_srs" "EPSG:3750 EPSG:4326"
              "wfs_enable_request" "*"
              "gml_include_items" "all"
              "gml_featureid" "objectid"
            END
          END
        END
        """;
}
