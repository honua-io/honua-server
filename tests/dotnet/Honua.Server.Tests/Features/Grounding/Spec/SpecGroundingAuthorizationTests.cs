// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using FluentAssertions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Hosting;

namespace Honua.Server.Tests.Features.Grounding.Spec;

// The shared grounding fixture runs with the development auth bypass, so these cases use a
// host with real admin gating and keys minted per principal profile.
[Collection("Database")]
[SecurityTest]
[Protocol(global::Honua.TestKit.Constants.ProtocolNames.Grounding)]
public sealed class SpecGroundingAuthorizationTests(SpecGroundingAuthorizationTests.GatedHost host)
    : IClassFixture<SpecGroundingAuthorizationTests.GatedHost>
{
    private const string MutateBody = """{"spec":{},"turn":"use rivers as rivers"}""";
    private const string SummarizeBody = """{"spec":{}}""";

    public sealed class GatedHost : IAsyncLifetime
    {
        private const string BootstrapAdminKey = "spec-grounding-authorization-bootstrap-key";

        internal WebAppFixture Fixture { get; } = new WebAppFixture()
            .WithTestLicense(HonuaEdition.Pro)
            .ConfigureWebHost(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", BootstrapAdminKey);
            })
            .ReplaceService<IMetadataV2GraphProvider>(SpecGroundingTestSupport.CreateGraphProvider(
                SpecGroundingTestSupport.CreateLayer(1, "Rivers")));

        internal IReadOnlyDictionary<string, string> Keys { get; private set; } =
            new Dictionary<string, string>();

        public async Task InitializeAsync()
        {
            await Fixture.InitializeAsync();
            var store = Fixture.GetService<IAdminApiKeyStore>();
            Keys = new Dictionary<string, string>
            {
                ["non-admin"] = await IssueKeyAsync(store, "non-admin", "read:layers"),
                ["admin-read"] = await IssueKeyAsync(store, "admin-read", "admin:read"),
                ["admin-write"] = await IssueKeyAsync(store, "admin-write", "admin:write")
            };
        }

        public Task DisposeAsync() => Fixture.DisposeAsync();

        private static async Task<string> IssueKeyAsync(IAdminApiKeyStore store, string name, string permission)
        {
            var result = await store.CreateAsync(
                $"spec-grounding-{name}",
                [permission],
                expiresAt: null,
                createdBy: "spec-grounding-authorization",
                CancellationToken.None);
            return result.Key;
        }
    }

    [IntegrationTheory]
    [Operation(Operations.Security)]
    [Endpoint("POST /v1/grounding/spec/mutate")]
    [Endpoint("POST /v1/grounding/spec/summarize")]
    [InlineData("mutate", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("mutate", "non-admin", HttpStatusCode.Forbidden)]
    [InlineData("mutate", "admin-read", HttpStatusCode.Forbidden)]
    [InlineData("mutate", "admin-write", HttpStatusCode.OK)]
    [InlineData("summarize", "anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("summarize", "non-admin", HttpStatusCode.Forbidden)]
    [InlineData("summarize", "admin-read", HttpStatusCode.Forbidden)]
    [InlineData("summarize", "admin-write", HttpStatusCode.OK)]
    public async Task GroundingRoute_PerPrincipal_RequiresRoleAdminWrite(
        string endpoint, string profile, HttpStatusCode expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/grounding/spec/{endpoint}")
        {
            Content = new StringContent(
                endpoint == "mutate" ? MutateBody : SummarizeBody,
                Encoding.UTF8,
                "application/json")
        };
        if (host.Keys.TryGetValue(profile, out var key))
        {
            request.Headers.Add("X-API-Key", key);
        }

        using var response = await host.Fixture.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expected, body);
        if (expected != HttpStatusCode.OK)
        {
            body.Should().NotContain("next_spec", "a refused caller must not receive a grounded spec");
            body.Should().NotContain("catalog:layer:1", "a refused caller must not learn catalog layer ids");
            body.Should().NotContain("section_summaries");
        }
    }
}
