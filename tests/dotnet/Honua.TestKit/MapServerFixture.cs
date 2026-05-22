// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.IO.Compression;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Xunit;

namespace Honua.TestKit;

/// <summary>
/// Shared MapServer fixture for integration tests that require a live MapServer instance.
/// Uses Testcontainers to manage the MapServer container lifecycle.
/// </summary>
public sealed class MapServerFixture : IAsyncLifetime
{
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static INetwork? _sharedNetwork;
    private static IContainer? _sharedMapServerContainer;
    private static IContainer? _sharedMapCacheContainer;
    private static string? _sharedEndpointUrl;
    private static string? _sharedWmtsEndpointUrl;
    private static string? _sharedStagingDirectory;
    private static int _sharedRefCount;
    private static bool _sharedInitialized;

    private const string MapServerImage = "camptocamp/mapserver:8.0";
    private const string MapCacheImage = "camptocamp/mapcache:1.10";
    private const int MapServerPort = 80;
    private const int MapCachePort = 80;
    private const string ContainerMapFilePath = "/etc/mapserver/mapserver.map";
    private const string MapServerNetworkAlias = "mapserver";
    private const string MapCacheWmtsPath = "/mapcache/wmts";

    /// <summary>
    /// Name of the seeded MapServer polygon layer.
    /// </summary>
    public const string LayerName = "tsunami_zones";

    /// <summary>
    /// Gets the MapServer OGC endpoint URL with the fixture mapfile parameter applied.
    /// </summary>
    public string EndpointUrl => _sharedEndpointUrl ?? throw new InvalidOperationException("MapServer fixture not initialized.");

    /// <summary>
    /// Gets the MapCache-backed WMTS endpoint URL for the seeded MapServer layer.
    /// </summary>
    public string WmtsEndpointUrl => _sharedWmtsEndpointUrl ?? throw new InvalidOperationException("MapServer fixture not initialized.");

    /// <inheritdoc />
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

    /// <inheritdoc />
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
            _sharedNetwork = new NetworkBuilder().Build();
            await _sharedNetwork.CreateAsync().ConfigureAwait(false);

            _sharedMapServerContainer = new ContainerBuilder()
                .WithImage(MapServerImage)
                .WithNetwork(_sharedNetwork)
                .WithNetworkAliases(MapServerNetworkAlias)
                .WithPortBinding(MapServerPort, true)
                .WithResourceMapping(new DirectoryInfo(stagingDirectory.DataDirectory), "/etc/mapserver/data")
                .WithResourceMapping(new FileInfo(stagingDirectory.MapFilePath), "/etc/mapserver/")
                .Build();

            await _sharedMapServerContainer.StartAsync().ConfigureAwait(false);

            var mapServerPort = _sharedMapServerContainer.GetMappedPublicPort(MapServerPort);
            _sharedEndpointUrl = $"http://127.0.0.1:{mapServerPort}/?map={Uri.EscapeDataString(ContainerMapFilePath)}";

            await WaitForMapServerReadyAsync(_sharedEndpointUrl).ConfigureAwait(false);

            _sharedMapCacheContainer = new ContainerBuilder()
                .WithImage(MapCacheImage)
                .WithNetwork(_sharedNetwork)
                .WithPortBinding(MapCachePort, true)
                .WithResourceMapping(new FileInfo(stagingDirectory.MapCacheFilePath), "/etc/mapcache/")
                .Build();

            await _sharedMapCacheContainer.StartAsync().ConfigureAwait(false);

            var mapCachePort = _sharedMapCacheContainer.GetMappedPublicPort(MapCachePort);
            _sharedWmtsEndpointUrl = $"http://127.0.0.1:{mapCachePort}{MapCacheWmtsPath}";

            await WaitForMapCacheReadyAsync(_sharedWmtsEndpointUrl).ConfigureAwait(false);
        }
        catch
        {
            await ResetSharedStateAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ResetSharedStateAsync()
    {
        if (_sharedMapCacheContainer is not null)
        {
            await _sharedMapCacheContainer.DisposeAsync().ConfigureAwait(false);
        }

        if (_sharedMapServerContainer is not null)
        {
            await _sharedMapServerContainer.DisposeAsync().ConfigureAwait(false);
        }

        if (_sharedNetwork is not null)
        {
            await _sharedNetwork.DisposeAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(_sharedStagingDirectory) && Directory.Exists(_sharedStagingDirectory))
        {
            Directory.Delete(_sharedStagingDirectory, recursive: true);
        }

        _sharedNetwork = null;
        _sharedMapServerContainer = null;
        _sharedMapCacheContainer = null;
        _sharedEndpointUrl = null;
        _sharedWmtsEndpointUrl = null;
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

        var mapCacheFilePath = Path.Combine(rootDirectory, "mapcache.xml");
        File.WriteAllText(mapCacheFilePath, MapCacheXmlContent);

        return new MapServerStagingDirectory(rootDirectory, dataDirectory, mapFilePath, mapCacheFilePath);
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

    private static async Task WaitForMapCacheReadyAsync(string endpointUrl)
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
                    $"{endpointUrl}?SERVICE=WMTS&REQUEST=GetCapabilities&VERSION=1.0.0").ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

                if (response.IsSuccessStatusCode &&
                    body.Contains("<Capabilities", StringComparison.OrdinalIgnoreCase) &&
                    body.Contains(LayerName, StringComparison.Ordinal))
                {
                    return;
                }

                lastError = new InvalidOperationException(
                    $"MapCache readiness returned {(int)response.StatusCode} {response.ReasonPhrase}.");
            }
            catch (Exception ex) when (IsTransientStartupFailure(ex))
            {
                lastError = ex;
            }

            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }

        var errorDetails = lastError is null ? string.Empty : $" Last error: {lastError.Message}";
        throw new TimeoutException($"Timed out waiting for MapCache to become ready.{errorDetails}");
    }

    private static bool IsTransientStartupFailure(Exception exception)
        => exception is HttpRequestException or TaskCanceledException or TimeoutException;

    private sealed record MapServerStagingDirectory(
        string RootDirectory,
        string DataDirectory,
        string MapFilePath,
        string MapCacheFilePath);

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
              "ows_srs" "EPSG:3750 EPSG:4326 EPSG:3857"
              "ows_enable_request" "*"
              "wms_enable_request" "*"
              "wms_srs" "EPSG:3750 EPSG:4326 EPSG:3857"
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
              "wms_srs" "EPSG:3750 EPSG:4326 EPSG:3857"
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

    private const string MapCacheXmlContent = """
        <?xml version="1.0" encoding="UTF-8"?>
        <mapcache>
          <cache name="disk" type="disk">
            <base>/tmp</base>
            <symlink_blank/>
          </cache>

          <source name="mapserver" type="wms">
            <getmap>
              <params>
                <MAP>/etc/mapserver/mapserver.map</MAP>
                <FORMAT>image/png</FORMAT>
                <LAYERS>tsunami_zones</LAYERS>
                <TRANSPARENT>true</TRANSPARENT>
              </params>
            </getmap>
            <http>
              <url>http://mapserver/</url>
            </http>
          </source>

          <tileset name="tsunami_zones">
            <source>mapserver</source>
            <cache>disk</cache>
            <grid>GoogleMapsCompatible</grid>
            <format>PNG</format>
            <metatile>1 1</metatile>
            <metabuffer>0</metabuffer>
            <expires>3600</expires>
          </tileset>

          <default_format>PNG</default_format>
          <service type="wmts" enabled="true"/>
          <errors>report</errors>
          <locker type="disk">
            <directory>/tmp</directory>
            <timeout>300</timeout>
          </locker>
        </mapcache>
        """;
}
