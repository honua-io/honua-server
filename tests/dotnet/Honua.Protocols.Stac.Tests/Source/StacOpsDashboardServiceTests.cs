// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using FluentAssertions;

namespace Honua.Server.Tests.Features.Protocols.Stac;

/// <summary>
/// Unit tests for the hosted STAC operations demo dashboard service.
/// </summary>
public sealed class StacOpsDashboardServiceTests
{
    [Fact]
    public async Task LoadAsync_StringOnlySortField_ReportsWarningInsteadOfPass()
    {
        var snapshot = await LoadSnapshotAsync(HandleRequest);
        var sortProbe = GetQueryProbe(snapshot, "Sort probe");

        sortProbe.GetType().GetProperty("Level")!.GetValue(sortProbe)!.ToString().Should().Be("Warn");
        sortProbe.GetType().GetProperty("Verdict")!.GetValue(sortProbe).Should().Be("Order not asserted for 'platform'");
        sortProbe.GetType().GetProperty("Detail")!.GetValue(sortProbe).Should().BeOfType<string>()
            .Which.Should().Contain("not uniformly numeric or RFC 3339 timestamps");
    }

    [Fact]
    public async Task LoadAsync_FieldsProbeSkipsMandatoryDatetimeProperty()
    {
        var snapshot = await LoadSnapshotAsync(HandleRequestWithTemporalDriftFixture);
        var fieldsProbe = GetQueryProbe(snapshot, "Fields probe");

        fieldsProbe.GetType().GetProperty("Level")!.GetValue(fieldsProbe)!.ToString().Should().Be("Pass");
        fieldsProbe.GetType().GetProperty("Verdict")!.GetValue(fieldsProbe).Should().Be("Property 'platform' excluded");
        fieldsProbe.GetType().GetProperty("RequestPath")!.GetValue(fieldsProbe).Should().BeOfType<string>()
            .Which.Should().Contain("-platform")
            .And.NotContain("-datetime");
    }

    [Fact]
    public async Task LoadAsync_MixedOffsetObservedDatetimes_UsesLatestInstantForTemporalDrift()
    {
        var snapshot = await LoadSnapshotAsync(HandleRequestWithTemporalDriftFixture);
        var collection = GetCollection(snapshot, "alpha");

        collection.GetType().GetProperty("CachedTemporalDriftDetected")!.GetValue(collection).Should().Be(true);
        collection.GetType().GetProperty("LatestObservedDatetime")!.GetValue(collection).Should().Be("2026-03-30T23:45:00-10:00");
        collection.GetType().GetProperty("AdvertisedLatestDatetime")!.GetValue(collection).Should().Be("2026-03-31T00:00:00Z");

        var driftNotes = ((System.Collections.IEnumerable)collection.GetType().GetProperty("DriftNotes")!.GetValue(collection)!)
            .Cast<string>()
            .ToArray();
        driftNotes.Should().Contain(note => note.Contains("Live item timestamps reached 2026-03-30T23:45:00-10:00", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_StaleCollectionListing_KeepsListingAndDetailValidatorsSeparate()
    {
        var snapshot = await LoadSnapshotAsync(HandleRequestWithTemporalDriftFixture);
        var collection = GetCollection(snapshot, "alpha");

        collection.GetType().GetProperty("CollectionListETag")!.GetValue(collection).Should().Be("\"collections-stale\"");
        collection.GetType().GetProperty("DetailETag")!.GetValue(collection).Should().Be("\"collection-alpha-fresh\"");

        var driftNotes = ((System.Collections.IEnumerable)collection.GetType().GetProperty("DriftNotes")!.GetValue(collection)!)
            .Cast<string>()
            .ToArray();
        driftNotes.Should().Contain(note => note.Contains("cached /stac/collections extent", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadAsync_DatetimePropertyPresentButNull_DoesNotFlagMissingDatetime()
    {
        var snapshot = await LoadSnapshotAsync(HandleRequestWithNullDatetimeFixture);
        var collection = GetCollection(snapshot, "alpha");

        collection.GetType().GetProperty("MissingDatetimeCount")!.GetValue(collection).Should().Be(0);
        collection.GetType().GetProperty("Detail")!.GetValue(collection).Should().BeOfType<string>()
            .Which.Should().NotContainEquivalentOf("missing datetime");

        var driftNotes = ((System.Collections.IEnumerable)collection.GetType().GetProperty("DriftNotes")!.GetValue(collection)!)
            .Cast<string>()
            .ToArray();
        driftNotes.Should().NotContain(note => note.Contains("required datetime field", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LoadAsync_DatetimePropertyAbsent_FlagsMissingDatetime()
    {
        var snapshot = await LoadSnapshotAsync(HandleRequest);
        var collection = GetCollection(snapshot, "alpha");

        collection.GetType().GetProperty("MissingDatetimeCount")!.GetValue(collection).Should().Be(2);
        collection.GetType().GetProperty("Detail")!.GetValue(collection).Should().BeOfType<string>()
            .Which.Should().Contain("2 sampled item(s) are missing datetime.");

        var driftNotes = ((System.Collections.IEnumerable)collection.GetType().GetProperty("DriftNotes")!.GetValue(collection)!)
            .Cast<string>()
            .ToArray();
        driftNotes.Should().Contain(note => note.Contains("required datetime field", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<object> LoadSnapshotAsync(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        using var client = new HttpClient(new StubHttpMessageHandler(responder))
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
        return snapshot!;
    }

    private static object GetQueryProbe(object snapshot, string label)
    {
        return ((System.Collections.IEnumerable)snapshot.GetType().GetProperty("QueryProbes")!.GetValue(snapshot)!)
            .Cast<object>()
            .Single(probe => string.Equals(
                probe.GetType().GetProperty("Label")!.GetValue(probe) as string,
                label,
                StringComparison.Ordinal));
    }

    private static object GetCollection(object snapshot, string collectionId)
    {
        return ((System.Collections.IEnumerable)snapshot.GetType().GetProperty("Collections")!.GetValue(snapshot)!)
            .Cast<object>()
            .Single(collection => string.Equals(
                collection.GetType().GetProperty("Id")!.GetValue(collection) as string,
                collectionId,
                StringComparison.Ordinal));
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
                """,
                "\"catalog-v1\""),
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
                """,
                "\"collections-stale\""),
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
                """,
                "\"collection-alpha-fresh\""),
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
            "/stac/search?collections=alpha&limit=5&filter=quality_score%20%3E%3D%2070&filter-lang=cql2-text" or
            "/stac/search?collections=alpha&limit=5&filter=quality_score%2520%253E%253D%252070&filter-lang=cql2-text" => CreateJsonResponse(
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

    private static HttpResponseMessage HandleRequestWithTemporalDriftFixture(HttpRequestMessage request)
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
                """,
                "\"catalog-v1\""),
            "/stac/collections" => CreateJsonResponse(
                """
                {
                  "collections": [
                    {
                      "id": "alpha",
                      "title": "Alpha Collection",
                      "license": "proprietary",
                      "extent": {
                        "temporal": {
                          "interval": [["2026-03-01T00:00:00Z", "2026-03-31T00:00:00Z"]]
                        }
                      },
                      "links": [
                        { "rel": "items", "href": "https://demo.example/stac/collections/alpha/items" }
                      ]
                    }
                  ],
                  "links": []
                }
                """,
                "\"collections-stale\""),
            "/stac/collections/alpha" => CreateJsonResponse(
                """
                {
                  "id": "alpha",
                  "title": "Alpha Collection",
                  "license": "proprietary",
                  "extent": {
                    "temporal": {
                      "interval": [["2026-03-01T00:00:00Z", "2026-03-31T00:00:00Z"]]
                    }
                  },
                  "links": [
                    { "rel": "items", "href": "https://demo.example/stac/collections/alpha/items" }
                  ]
                }
                """,
                "\"collection-alpha-fresh\""),
            "/stac/collections/alpha/items?limit=2" or "/stac/collections/alpha/items%3Flimit=2" => CreateJsonResponse(CreateTemporalDriftItemsResponse()),
            "/stac/search?limit=50" => CreateJsonResponse(CreateTemporalDriftItemsResponse()),
            "/stac/search?collections=alpha&limit=3" => CreateJsonResponse(CreateTemporalDriftItemsResponse()),
            "/stac/search?collections=alpha&limit=2" => CreateJsonResponse(CreateTemporalDriftItemsResponse()),
            "/stac/search?collections=alpha&limit=3&sortby=-quality_score" => CreateJsonResponse(
                """
                {
                  "features": [
                    {
                      "id": "item-1",
                      "collection": "alpha",
                      "properties": {
                        "datetime": "2026-03-31T00:30:00+14:00",
                        "quality_score": 90,
                        "platform": "drone-alpha"
                      },
                      "assets": {}
                    },
                    {
                      "id": "item-2",
                      "collection": "alpha",
                      "properties": {
                        "datetime": "2026-03-30T23:45:00-10:00",
                        "quality_score": 70,
                        "platform": "drone-beta"
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
                      "properties": {
                        "datetime": "2026-03-31T00:30:00+14:00",
                        "quality_score": 90
                      },
                      "assets": {}
                    }
                  ],
                  "links": [],
                  "numberMatched": 1,
                  "numberReturned": 1
                }
                """),
            "/stac/search?collections=alpha&limit=5&filter=quality_score%20%3E%3D%2070&filter-lang=cql2-text" or
            "/stac/search?collections=alpha&limit=5&filter=quality_score%2520%253E%253D%252070&filter-lang=cql2-text" => CreateJsonResponse(
                """
                {
                  "features": [
                    {
                      "id": "item-1",
                      "collection": "alpha",
                      "properties": {
                        "datetime": "2026-03-31T00:30:00+14:00",
                        "quality_score": 90,
                        "platform": "drone-alpha"
                      },
                      "assets": {}
                    },
                    {
                      "id": "item-2",
                      "collection": "alpha",
                      "properties": {
                        "datetime": "2026-03-30T23:45:00-10:00",
                        "quality_score": 70,
                        "platform": "drone-beta"
                      },
                      "assets": {}
                    }
                  ],
                  "links": [],
                  "numberMatched": 2,
                  "numberReturned": 2
                }
                """),
            _ => throw new InvalidOperationException($"Unhandled request path: {pathAndQuery}")
        };
    }

    private static HttpResponseMessage HandleRequestWithNullDatetimeFixture(HttpRequestMessage request)
    {
        var pathAndQuery = (request.RequestUri?.PathAndQuery ?? string.Empty)
            .Replace("%3F", "?", StringComparison.OrdinalIgnoreCase);

        return pathAndQuery switch
        {
            "/stac/collections/alpha/items?limit=2" or "/stac/collections/alpha/items%3Flimit=2" => CreateJsonResponse(CreateNullDatetimeItemsResponse()),
            "/stac/search?limit=50" => CreateJsonResponse(CreateNullDatetimeItemsResponse()),
            "/stac/search?collections=alpha&limit=3" => CreateJsonResponse(CreateNullDatetimeItemsResponse()),
            "/stac/search?collections=alpha&limit=2" => CreateJsonResponse(CreateNullDatetimeItemsResponse()),
            "/stac/search?collections=alpha&limit=3&sortby=-datetime" => CreateJsonResponse(CreateNullDatetimeItemsResponse()),
            _ => HandleRequest(request)
        };
    }

    private static string CreateTemporalDriftItemsResponse()
    {
        return
            """
            {
              "features": [
                {
                  "id": "item-1",
                  "collection": "alpha",
                  "properties": {
                    "datetime": "2026-03-31T00:30:00+14:00",
                    "quality_score": 90,
                    "platform": "drone-alpha"
                  },
                  "assets": {}
                },
                {
                  "id": "item-2",
                  "collection": "alpha",
                  "properties": {
                    "datetime": "2026-03-30T23:45:00-10:00",
                    "quality_score": 70,
                    "platform": "drone-beta"
                  },
                  "assets": {}
                }
              ],
              "links": [],
              "numberMatched": 2,
              "numberReturned": 2
            }
            """;
    }

    private static string CreateNullDatetimeItemsResponse()
    {
        return
            """
            {
              "features": [
                {
                  "id": "item-1",
                  "collection": "alpha",
                  "properties": {
                    "datetime": null,
                    "platform": "drone-beta"
                  },
                  "assets": {}
                },
                {
                  "id": "item-2",
                  "collection": "alpha",
                  "properties": {
                    "datetime": null,
                    "platform": "drone-alpha"
                  },
                  "assets": {}
                }
              ],
              "links": [],
              "numberMatched": 2,
              "numberReturned": 2
            }
            """;
    }

    private static HttpResponseMessage CreateTextResponse(string body)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        };
    }

    private static HttpResponseMessage CreateJsonResponse(string body, string? etag = null)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (!string.IsNullOrWhiteSpace(etag))
        {
            response.Headers.ETag = EntityTagHeaderValue.Parse(etag);
        }

        return response;
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
