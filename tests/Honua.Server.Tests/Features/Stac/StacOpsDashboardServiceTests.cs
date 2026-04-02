// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Text;
using FluentAssertions;

namespace Honua.Server.Tests.Features.Stac;

/// <summary>
/// Unit tests for the hosted STAC operations demo dashboard service.
/// </summary>
public sealed class StacOpsDashboardServiceTests
{
    [Fact]
    public async Task LoadAsync_StringOnlySortField_ReportsWarningInsteadOfPass()
    {
        using var client = new HttpClient(new StubHttpMessageHandler(HandleRequest))
        {
            BaseAddress = new Uri("https://demo.example/")
        };
        var sampleAssembly = Assembly.Load("Honua.StacOpsDemo");
        var serviceType = sampleAssembly.GetType("Honua.StacOpsDemo.Services.StacOpsDashboardService", throwOnError: true)!;
        var constructor = serviceType.GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            [typeof(HttpClient)],
            modifiers: null);
        constructor.Should().NotBeNull();

        var service = constructor!.Invoke([client]);
        var loadAsync = serviceType.GetMethod("LoadAsync", BindingFlags.Instance | BindingFlags.Public);
        loadAsync.Should().NotBeNull();

        var loadTask = (Task)loadAsync!.Invoke(service, ["https://demo.example", CancellationToken.None])!;
        await loadTask;

        var snapshot = loadTask.GetType().GetProperty("Result")!.GetValue(loadTask);
        snapshot.Should().NotBeNull();

        var queryProbes = ((System.Collections.IEnumerable)snapshot!.GetType().GetProperty("QueryProbes")!.GetValue(snapshot)!)
            .Cast<object>()
            .ToArray();
        var sortProbe = queryProbes.Single(probe => string.Equals(
            probe.GetType().GetProperty("Label")!.GetValue(probe) as string,
            "Sort probe",
            StringComparison.Ordinal));

        sortProbe.GetType().GetProperty("Level")!.GetValue(sortProbe)!.ToString().Should().Be("Warn");
        sortProbe.GetType().GetProperty("Verdict")!.GetValue(sortProbe).Should().Be("Order not asserted for 'platform'");
        sortProbe.GetType().GetProperty("Detail")!.GetValue(sortProbe).Should().BeOfType<string>()
            .Which.Should().Contain("not uniformly numeric or RFC 3339 timestamps");
    }

    private static HttpResponseMessage HandleRequest(HttpRequestMessage request)
    {
        var pathAndQuery = (request.RequestUri?.PathAndQuery ?? string.Empty)
            .Replace("%3F", "?", StringComparison.OrdinalIgnoreCase);
        return pathAndQuery switch
        {
            "/healthz/live" => CreateTextResponse("Healthy"),
            "/healthz/ready" => CreateTextResponse("Ready"),
            "/stac" => CreateJsonResponse(
                """
                {
                  "conformsTo": ["https://api.stacspec.org/v1.0.0/core"],
                  "links": [
                    { "rel": "child", "href": "https://demo.example/stac/collections/alpha" }
                  ]
                }
                """),
            "/stac/collections" => CreateJsonResponse(
                """
                {
                  "collections": [
                    {
                      "id": "alpha",
                      "title": "Alpha Collection",
                      "license": "proprietary",
                      "keywords": ["demo"],
                      "extent": {
                        "temporal": {
                          "interval": [["2026-03-01T00:00:00Z", "2026-03-03T00:00:00Z"]]
                        }
                      },
                      "links": [
                        { "rel": "items", "href": "https://demo.example/stac/collections/alpha/items" }
                      ]
                    }
                  ],
                  "links": []
                }
                """),
            "/stac/collections/alpha" => CreateJsonResponse(
                """
                {
                  "id": "alpha",
                  "title": "Alpha Collection",
                  "license": "proprietary",
                  "keywords": ["demo"],
                  "extent": {
                    "temporal": {
                      "interval": [["2026-03-01T00:00:00Z", "2026-03-03T00:00:00Z"]]
                    }
                  },
                  "links": [
                    { "rel": "items", "href": "https://demo.example/stac/collections/alpha/items" }
                  ]
                }
                """),
            "/stac/collections/alpha/items?limit=2" or "/stac/collections/alpha/items%3Flimit=2" => CreateJsonResponse(
                """
                {
                  "features": [
                    {
                      "id": "item-1",
                      "collection": "alpha",
                      "properties": {
                        "platform": "drone-beta"
                      },
                      "assets": {}
                    },
                    {
                      "id": "item-2",
                      "collection": "alpha",
                      "properties": {
                        "platform": "drone-alpha"
                      },
                      "assets": {}
                    }
                  ],
                  "links": [],
                  "numberMatched": 2,
                  "numberReturned": 2
                }
                """),
            "/stac/search?limit=50" => CreateJsonResponse(
                """
                {
                  "features": [
                    {
                      "id": "item-1",
                      "collection": "alpha",
                      "properties": {
                        "platform": "drone-beta"
                      },
                      "assets": {}
                    }
                  ],
                  "links": [],
                  "numberMatched": 1,
                  "numberReturned": 1
                }
                """),
            "/stac/search?collections=alpha&limit=3" => CreateJsonResponse(
                """
                {
                  "features": [
                    {
                      "id": "item-1",
                      "collection": "alpha",
                      "properties": {
                        "platform": "drone-beta"
                      },
                      "assets": {}
                    },
                    {
                      "id": "item-2",
                      "collection": "alpha",
                      "properties": {
                        "platform": "drone-alpha"
                      },
                      "assets": {}
                    }
                  ],
                  "links": [],
                  "numberMatched": 2,
                  "numberReturned": 2
                }
                """),
            "/stac/search?collections=alpha&limit=2" => CreateJsonResponse(
                """
                {
                  "features": [
                    {
                      "id": "item-1",
                      "collection": "alpha",
                      "properties": {
                        "platform": "drone-beta"
                      },
                      "assets": {}
                    },
                    {
                      "id": "item-2",
                      "collection": "alpha",
                      "properties": {
                        "platform": "drone-alpha"
                      },
                      "assets": {}
                    }
                  ],
                  "links": [],
                  "numberMatched": 2,
                  "numberReturned": 2
                }
                """),
            "/stac/search?collections=alpha&limit=3&sortby=-platform" => CreateJsonResponse(
                """
                {
                  "features": [
                    {
                      "id": "item-1",
                      "collection": "alpha",
                      "properties": {
                        "platform": "drone-beta"
                      },
                      "assets": {}
                    },
                    {
                      "id": "item-2",
                      "collection": "alpha",
                      "properties": {
                        "platform": "drone-alpha"
                      },
                      "assets": {}
                    }
                  ],
                  "links": [],
                  "numberMatched": 2,
                  "numberReturned": 2
                }
                """),
            "/stac/search?collections=alpha&limit=1&fields=properties%2C-platform" or
            "/stac/search?collections=alpha&limit=1&fields=properties%252C-platform" => CreateJsonResponse(
                """
                {
                  "features": [
                    {
                      "id": "item-1",
                      "collection": "alpha",
                      "properties": {},
                      "assets": {}
                    }
                  ],
                  "links": [],
                  "numberMatched": 1,
                  "numberReturned": 1
                }
                """),
            "/stac/search?collections=alpha&limit=5&filter=quality_score%20%3E%3D%2070&filter-lang=cql2-text" => CreateJsonResponse(
                """
                {
                  "features": [],
                  "links": [],
                  "numberMatched": 0,
                  "numberReturned": 0
                }
                """),
            _ => throw new InvalidOperationException($"Unhandled request path: {pathAndQuery}")
        };
    }

    private static HttpResponseMessage CreateTextResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
    }

    private static HttpResponseMessage CreateJsonResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder = responder;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(_responder(request));
        }
    }
}
