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
    // The state object is process-wide and every mutation is serialized by _sharedLock.
    private static readonly SemaphoreSlim _sharedLock = new(1, 1);
    private static readonly MapServerSharedState SharedState = new();

    private sealed class MapServerSharedState
    {
        public INetwork? SharedNetwork { get; set; }
        public IContainer? SharedMapServerContainer { get; set; }
        public IContainer? SharedMapCacheContainer { get; set; }
        public string? SharedEndpointUrl { get; set; }
        public string? SharedWmtsEndpointUrl { get; set; }
        public string? SharedStagingDirectory { get; set; }
        public int SharedRefCount { get; set; }
        public bool SharedInitialized { get; set; }
    }

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
    public string EndpointUrl => SharedState.SharedEndpointUrl ?? throw new InvalidOperationException("MapServer fixture not initialized.");

    /// <summary>
    /// Gets the MapCache-backed WMTS endpoint URL for the seeded MapServer layer.
    /// </summary>
    public string WmtsEndpointUrl => SharedState.SharedWmtsEndpointUrl ?? throw new InvalidOperationException("MapServer fixture not initialized.");

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        await _sharedLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!SharedState.SharedInitialized)
            {
                await StartContainerAsync().ConfigureAwait(false);
                SharedState.SharedInitialized = true;
            }

            SharedState.SharedRefCount++;
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
            if (SharedState.SharedRefCount > 0)
            {
                SharedState.SharedRefCount--;
            }

            if (SharedState.SharedRefCount == 0 && SharedState.SharedInitialized)
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
        SharedState.SharedStagingDirectory = stagingDirectory.RootDirectory;

        try
        {
            SharedState.SharedNetwork = new NetworkBuilder().Build();
            await SharedState.SharedNetwork.CreateAsync().ConfigureAwait(false);

            SharedState.SharedMapServerContainer = new ContainerBuilder()
                .WithImage(MapServerImage)
                .WithNetwork(SharedState.SharedNetwork)
                .WithNetworkAliases(MapServerNetworkAlias)
                .WithPortBinding(MapServerPort, true)
                .WithResourceMapping(new DirectoryInfo(stagingDirectory.DataDirectory), "/etc/mapserver/data")
                .WithResourceMapping(new FileInfo(stagingDirectory.MapFilePath), "/etc/mapserver/")
                .Build();

            await SharedState.SharedMapServerContainer.StartAsync().ConfigureAwait(false);

            var mapServerPort = SharedState.SharedMapServerContainer.GetMappedPublicPort(MapServerPort);
            SharedState.SharedEndpointUrl = $"http://127.0.0.1:{mapServerPort}/?map={Uri.EscapeDataString(ContainerMapFilePath)}";

            await WaitForMapServerReadyAsync(SharedState.SharedEndpointUrl).ConfigureAwait(false);

            SharedState.SharedMapCacheContainer = new ContainerBuilder()
                .WithImage(MapCacheImage)
                .WithNetwork(SharedState.SharedNetwork)
                .WithPortBinding(MapCachePort, true)
                .WithResourceMapping(new FileInfo(stagingDirectory.MapCacheFilePath), "/etc/mapcache/")
                .Build();

            await SharedState.SharedMapCacheContainer.StartAsync().ConfigureAwait(false);

            var mapCachePort = SharedState.SharedMapCacheContainer.GetMappedPublicPort(MapCachePort);
            SharedState.SharedWmtsEndpointUrl = $"http://127.0.0.1:{mapCachePort}{MapCacheWmtsPath}";

            await WaitForMapCacheReadyAsync(SharedState.SharedWmtsEndpointUrl).ConfigureAwait(false);
        }
        catch
        {
            await ResetSharedStateAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ResetSharedStateAsync()
    {
        if (SharedState.SharedMapCacheContainer is not null)
        {
            await SharedState.SharedMapCacheContainer.DisposeAsync().ConfigureAwait(false);
        }

        if (SharedState.SharedMapServerContainer is not null)
        {
            await SharedState.SharedMapServerContainer.DisposeAsync().ConfigureAwait(false);
        }

        if (SharedState.SharedNetwork is not null)
        {
            await SharedState.SharedNetwork.DisposeAsync().ConfigureAwait(false);
        }

        if (!string.IsNullOrWhiteSpace(SharedState.SharedStagingDirectory) && Directory.Exists(SharedState.SharedStagingDirectory))
        {
            Directory.Delete(SharedState.SharedStagingDirectory, recursive: true);
        }

        SharedState.SharedNetwork = null;
        SharedState.SharedMapServerContainer = null;
        SharedState.SharedMapCacheContainer = null;
        SharedState.SharedEndpointUrl = null;
        SharedState.SharedWmtsEndpointUrl = null;
        SharedState.SharedStagingDirectory = null;
        SharedState.SharedInitialized = false;
    }

    // All Path.Combine calls below join a generated staging directory with fixed
    // literal segments (a GUID-suffixed folder name or file names), so none can drop
    // an earlier argument.
    private static MapServerStagingDirectory CreateStagingDirectory()
    {
        var rootDirectory = Path.Join(Path.GetTempPath(), $"honua-mapserver-{Guid.NewGuid():N}");
        var dataDirectory = Path.Join(rootDirectory, "data");
        Directory.CreateDirectory(dataDirectory);

        ZipFile.ExtractToDirectory(
            ReferenceServerTestData.ResolveTsunamiEvacuationZonesZipPath(),
            dataDirectory,
            overwriteFiles: true);

        File.WriteAllText(Path.Join(dataDirectory, "featureinfo.html"), "zone=[zone_type]\nisland=[island]\n");

        var mapFilePath = Path.Join(rootDirectory, "mapserver.map");
        File.WriteAllText(mapFilePath, MapFileContent);

        var mapCacheFilePath = Path.Join(rootDirectory, "mapcache.xml");
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
