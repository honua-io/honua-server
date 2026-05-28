// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Migration.Domain;
using Honua.Core.Features.FileImport.Domain;
using Honua.Core.Features.Migration.Services;
using Honua.Core.Features.FileImport.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Core.Tests.Features.Import;

public sealed class MigrationRequestCountingHandlerTests
{
    [Fact]
    public async Task SendAsync_WithAmbientRecorder_IncrementsSourceRequestPerCall()
    {
        var recorder = new MigrationRunMetricsRecorder();
        var counting = new MigrationRequestCountingHandler(NullLogger<MigrationRequestCountingHandler>.Instance)
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("ok", Encoding.UTF8, "text/plain")
            })
        };

        using var client = new HttpClient(counting);

        using (MigrationMetricsAmbient.PushRecorder(recorder))
        {
            for (var i = 0; i < 4; i++)
            {
                using var response = await client.GetAsync("https://example.com/" + i);
                response.StatusCode.Should().Be(HttpStatusCode.OK);
            }
        }

        var totals = recorder.SnapshotTotals();
        totals.SourceRequestCount.Should().Be(4);
    }

    [Fact]
    public async Task SendAsync_WithContentLength_RecordsBytesRead()
    {
        var recorder = new MigrationRunMetricsRecorder();
        var counting = new MigrationRequestCountingHandler(NullLogger<MigrationRequestCountingHandler>.Instance)
        {
            InnerHandler = new StubHandler(_ =>
            {
                var content = new StringContent("hello-bytes", Encoding.UTF8, "text/plain");
                // StringContent surfaces ContentLength automatically from the encoded payload.
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            })
        };

        using var client = new HttpClient(counting);

        using (MigrationMetricsAmbient.PushRecorder(recorder))
        {
            using var response = await client.GetAsync("https://example.com/payload");
            response.Content.Headers.ContentLength.Should().BeGreaterThan(0);
        }

        var totals = recorder.SnapshotTotals();
        totals.SourceRequestCount.Should().Be(1);
        totals.BytesRead.Should().Be(Encoding.UTF8.GetByteCount("hello-bytes"));
    }

    [Fact]
    public async Task SendAsync_WithoutAmbientRecorder_DoesNotThrow()
    {
        var counting = new MigrationRequestCountingHandler(NullLogger<MigrationRequestCountingHandler>.Instance)
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        };

        using var client = new HttpClient(counting);

        var act = async () =>
        {
            using var response = await client.GetAsync("https://example.com/no-recorder");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SendAsync_WhenTransportFails_StillCountsRequest()
    {
        var recorder = new MigrationRunMetricsRecorder();
        var counting = new MigrationRequestCountingHandler(NullLogger<MigrationRequestCountingHandler>.Instance)
        {
            InnerHandler = new StubHandler(_ => throw new HttpRequestException("boom"))
        };

        using var client = new HttpClient(counting);

        using (MigrationMetricsAmbient.PushRecorder(recorder))
        {
            var act = async () => await client.GetAsync("https://example.com/fail");
            await act.Should().ThrowAsync<HttpRequestException>();
        }

        recorder.SnapshotTotals().SourceRequestCount.Should().Be(1);
    }

    [Fact]
    public async Task DiscoveryThroughGeoServerRestClient_RecordsMultipleSourceRequests()
    {
        var recorder = new MigrationRunMetricsRecorder();
        var counting = new MigrationRequestCountingHandler(NullLogger<MigrationRequestCountingHandler>.Instance)
        {
            InnerHandler = new MultiCallGeoServerHandler()
        };

        using var httpClient = new HttpClient(counting);
        var client = new GeoServerRestClient(
            httpClient,
            NullLogger<GeoServerRestClient>.Instance,
            (_, _) => Task.FromResult(new[] { IPAddress.Parse("93.184.216.34") }));

        using (MigrationMetricsAmbient.PushRecorder(recorder))
        {
            var info = await client.DiscoverServiceAsync(
                "https://example.com/geoserver/rest",
                username: null,
                password: null,
                includeCompatibilityAnalysis: false,
                includeStyleContent: false,
                timeoutSeconds: 5,
                maxRetryAttempts: 0,
                allowUnsafeLocalUrls: false,
                CancellationToken.None);

            info.Workspaces.Should().NotBeEmpty();
        }

        var totals = recorder.SnapshotTotals();
        totals.SourceRequestCount.Should().NotBeNull();
        // version, settings, workspaces, per-workspace metadata/datastores/coveragestores/layers/layergroups/styles,
        // styles index + per-workspace styles entries. The exact count depends on discovery internals; assert >1
        // to guarantee we are counting at the HTTP boundary (per request), not once for the whole discovery.
        totals.SourceRequestCount!.Value.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task SendAsync_PerRunScoping_RecordersDoNotShareCounts()
    {
        var runA = new MigrationRunMetricsRecorder();
        var runB = new MigrationRunMetricsRecorder();
        var counting = new MigrationRequestCountingHandler(NullLogger<MigrationRequestCountingHandler>.Instance)
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        };
        using var client = new HttpClient(counting);

        using (MigrationMetricsAmbient.PushRecorder(runA))
        {
            using var responseA1 = await client.GetAsync("https://example.com/a/1");
            using var responseA2 = await client.GetAsync("https://example.com/a/2");
        }

        using (MigrationMetricsAmbient.PushRecorder(runB))
        {
            using var responseB1 = await client.GetAsync("https://example.com/b/1");
        }

        // No ambient recorder — requests must not leak into either run.
        using var responseOrphan = await client.GetAsync("https://example.com/orphan");

        runA.SnapshotTotals().SourceRequestCount.Should().Be(2);
        runB.SnapshotTotals().SourceRequestCount.Should().Be(1);
    }

    [Fact]
    public async Task SendAsync_NoAmbientRecorder_DoesNotLeakIntoPreviouslyActiveRecorder()
    {
        var recorder = new MigrationRunMetricsRecorder();
        var counting = new MigrationRequestCountingHandler(NullLogger<MigrationRequestCountingHandler>.Instance)
        {
            InnerHandler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))
        };
        using var client = new HttpClient(counting);

        using (MigrationMetricsAmbient.PushRecorder(recorder))
        {
            using var counted = await client.GetAsync("https://example.com/counted");
        }

        // After the scope is disposed, additional calls must not be attributed to the recorder.
        using var uncounted = await client.GetAsync("https://example.com/uncounted");

        recorder.SnapshotTotals().SourceRequestCount.Should().Be(1);
    }

    [Fact]
    public void PushRecorder_NestedScopes_RestorePreviousRecorderOnDispose()
    {
        var outer = new MigrationRunMetricsRecorder();
        var inner = new MigrationRunMetricsRecorder();

        MigrationMetricsAmbient.Current.Should().BeNull();
        using (MigrationMetricsAmbient.PushRecorder(outer))
        {
            MigrationMetricsAmbient.Current.Should().BeSameAs(outer);
            using (MigrationMetricsAmbient.PushRecorder(inner))
            {
                MigrationMetricsAmbient.Current.Should().BeSameAs(inner);
            }

            MigrationMetricsAmbient.Current.Should().BeSameAs(outer);
        }

        MigrationMetricsAmbient.Current.Should().BeNull();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _factory;

        public StubHandler(Func<HttpRequestMessage, HttpResponseMessage> factory)
        {
            _factory = factory;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_factory(request));
    }

    private sealed class MultiCallGeoServerHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var (contentType, payload) = path switch
            {
                "/geoserver/rest/about/version.xml" => ("application/xml", """
                    <about>
                      <resource name="GeoServer">
                        <Version>2.28.0</Version>
                      </resource>
                    </about>
                    """),
                "/geoserver/rest/settings.json" => ("application/json", """{"global":{}}"""),
                "/geoserver/rest/workspaces.json" => ("application/json", """{"workspaces":{"workspace":[{"name":"demo"}]}}"""),
                "/geoserver/rest/workspaces/demo.json" => ("application/json", """{"workspace":{"name":"demo"}}"""),
                "/geoserver/rest/workspaces/demo/datastores.json" => ("application/json", """{"dataStores":{"dataStore":""}}"""),
                "/geoserver/rest/workspaces/demo/coveragestores.json" => ("application/json", """{"coverageStores":{"coverageStore":""}}"""),
                "/geoserver/rest/workspaces/demo/layers.json" => ("application/json", """{"layers":{"layer":""}}"""),
                "/geoserver/rest/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/workspaces/demo/layergroups.json" => ("application/json", """{"layerGroups":{"layerGroup":""}}"""),
                "/geoserver/rest/styles.json" => ("application/json", """{"styles":{"style":""}}"""),
                "/geoserver/rest/workspaces/demo/styles.json" => ("application/json", """{"styles":{"style":""}}"""),
                _ => throw new InvalidOperationException($"Unexpected GeoServer request path: {path}")
            };

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, contentType)
            });
        }
    }
}
