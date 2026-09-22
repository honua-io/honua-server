// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Security.Domain;
using Honua.Ai.Protocols.Mcp.MapTools;
using Honua.Ai.Protocols.Mcp.Tools;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Features.Protocols.Mcp;

/// <summary>
/// REST/MCP parity for feature READS against a real PostGIS fixture holding real
/// rows (#4389). MCP is a GA access path and the 2026.1 promise is that it applies
/// the same resource-access policy as REST, so the question this class asks is the
/// one that matters: for the SAME principal and the SAME layer, does MCP return
/// exactly what REST returns, and nothing more?
/// </summary>
/// <remarks>
/// Fixture shape (<c>tests/seed/server.yaml</c>):
/// <list type="bullet">
/// <item>layer 0 (<c>Test Layer</c>, objectids 1-5) carries an access policy that
/// admits only <see cref="ReaderRole"/> — the ALLOWED layer;</item>
/// <item>layer 1 (<c>Related Test Layer 1</c>, objectids 101-104) carries an access
/// policy that admits only <see cref="RestrictedRole"/> — the DENIED layer.</item>
/// </list>
/// Both layers hold real rows, so "denied" is provably "zero records out of a
/// populated layer" rather than "the layer happened to be empty".
/// <para>
/// The only substituted service is the generic MCP operator admission gate
/// (<see cref="IOperatorAuthorizationEvaluator"/>), which every MCP tool consults
/// before its own resource check. Admitting it isolates the decision under test —
/// the per-layer resource policy that REST and MCP are supposed to share — exactly
/// as <c>StudioMcpOwnershipParityIntegrationTests</c> does for Studio ownership.
/// The metadata graph, the filter parser, the feature-query pipeline, the REST
/// adapter, the MCP dispatcher and PostGIS all stay production services.
/// </para>
/// </remarks>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.Mcp)]
public sealed class McpRestFeatureReadParityIntegrationTests : IAsyncLifetime
{
    private const string ServiceId = "test";
    private const int AllowedLayerId = 0;
    private const int DeniedLayerId = 1;

    private const string ReaderRole = "feature-read-parity-reader";
    private const string RestrictedRole = "feature-read-parity-restricted";
    private const string UnrelatedRole = "feature-read-parity-nobody";

    /// <summary>Attribute value that only exists on the allowed layer's rows.</summary>
    private const string AllowedLayerMarker = "Test Feature";

    /// <summary>Attribute value that only exists on the denied layer's rows.</summary>
    private const string DeniedLayerMarker = "Related Feature";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_DEV_AUTH_ALLOW_BYPASS", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
        })
        .ConfigureServices(services =>
        {
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, ParityAuthHandler>(ParityAuthHandler.SchemeName, _ => { });
            services.PostConfigureAll<AuthenticationOptions>(options =>
            {
                options.DefaultAuthenticateScheme = ParityAuthHandler.SchemeName;
                options.DefaultChallengeScheme = ParityAuthHandler.SchemeName;
                options.DefaultScheme = ParityAuthHandler.SchemeName;
            });

            // honua_query_features is registered by AddMcpDataAccessSurface only when
            // IFeatureReader is already in the collection. The Test-environment host
            // defers provider registration to the fixture (TestInfrastructureRegistrationPolicy),
            // so the tool is absent from the integration host and has to be composed
            // explicitly here — the same thing ImportAuthorizationParityTests does for
            // IngestDatasetTool. The tool itself is the production type; only its
            // registration is supplied.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<IMcpTool, QueryFeaturesTool>());

            // Generic MCP operator admission only. The per-layer resource policy —
            // the decision this class is about — is left to the production path.
            services.RemoveAll<IOperatorAuthorizationEvaluator>();
            services.AddSingleton<IOperatorAuthorizationEvaluator, AdmitEveryOperatorRequest>();
        });

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();

        _fixture.UpdateV2ResourceMetadata(AllowedLayerId, accessPolicy: new AccessPolicy
        {
            AllowAnonymous = false,
            AllowAnonymousWrite = false,
            AllowedRoles = [ReaderRole],
        });

        _fixture.UpdateV2ResourceMetadata(DeniedLayerId, accessPolicy: new AccessPolicy
        {
            AllowAnonymous = false,
            AllowAnonymousWrite = false,
            AllowedRoles = [RestrictedRole],
        });
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    /// <summary>
    /// Acceptance criteria 1 and 2: one principal, one layer, both surfaces — the
    /// same record set, asserted against the literal seeded denominator so the two
    /// surfaces agreeing on the WRONG answer still fails; and neither surface
    /// returns a row from the layer the same principal is denied.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task FeatureRead_SamePrincipalOverRestAndMcp_ReturnsTheSameRecordSetAndNoDeniedLayerRows()
    {
        using var reader = CreateClient(ReaderRole);

        var rest = await ReadOverRestAsync(reader, AllowedLayerId);
        var mcp = await ReadOverMcpAsync(reader, AllowedLayerId);

        // Literal denominator first: both surfaces are measured against the seed,
        // not only against each other.
        rest.Select(record => record.ObjectId).Should().Equal(new long[] { 1, 2, 3, 4, 5 },
            "tests/seed/server.yaml publishes exactly five rows on the allowed layer");
        rest.Single(record => record.ObjectId == 1).Name.Should().Be(AllowedLayerMarker);
        rest.Single(record => record.ObjectId == 4).Category.Should().Be("sample");

        // Parity: identical record sets, field values and geometry, id for id.
        mcp.Select(record => record.ObjectId).Should().Equal(rest.Select(record => record.ObjectId),
            "MCP must return the same records as REST for the same principal and layer");
        mcp.Should().Equal(rest,
            "MCP must return the same field values and geometry as REST, not a superset or a variant");

        // A filtered read must agree too, so a surface that silently drops the
        // WHERE clause (and returns every row) is caught.
        var filteredRest = await ReadOverRestAsync(reader, AllowedLayerId, where: "category = 'test'");
        var filteredMcp = await ReadOverMcpAsync(reader, AllowedLayerId, where: "category = 'test'");
        filteredRest.Select(record => record.ObjectId).Should().Equal(new long[] { 1, 3, 5 });
        filteredMcp.Should().Equal(filteredRest);

        // Nothing from the denied layer leaked into either allowed-layer response.
        rest.Should().NotContain(record => record.ObjectId >= 101);
        mcp.Should().NotContain(record => record.ObjectId >= 101);

        // The same principal reading the DENIED layer is refused on both surfaces
        // and receives no records from either.
        // GeoServices reports denials as an Esri error envelope carried on HTTP 200,
        // so the refusal is asserted on error.code rather than the transport status.
        using var restDenied = await reader.GetAsync(QueryUrl(DeniedLayerId, "1=1"));
        var restDeniedBody = await restDenied.Content.ReadAsStringAsync();
        AssertGeoServicesForbidden(restDeniedBody);
        restDeniedBody.Should().NotContain(DeniedLayerMarker);

        var mcpDenied = await CallQueryFeaturesAsync(reader, DeniedLayerId);
        AssertZeroRecords(mcpDenied, DeniedLayerMarker);

        // ...and the denied layer really does hold rows, so "zero records" is a
        // refusal rather than an empty table.
        using var restricted = CreateClient(RestrictedRole);
        var restrictedRows = await ReadOverRestAsync(restricted, DeniedLayerId);
        restrictedRows.Select(record => record.ObjectId).Should().Equal(new long[] { 101, 102, 103, 104 });
    }

    /// <summary>
    /// Acceptance criterion 3: a denied principal receives ZERO RECORDS over MCP —
    /// not merely a refusal envelope. Asserted on a populated layer, over the
    /// record-bearing read, the count-only read and the raw response text.
    /// </summary>
    [IntegrationTest]
    [Operation(Operations.Query)]
    [Endpoint("GET /rest/services/{serviceId}/FeatureServer/{layerId}/query")]
    [Endpoint("POST /mcp tools/call honua_query_features")]
    [InterfaceOperation(TestProtocols.Mcp, "tools/call")]
    public async Task FeatureRead_DeniedPrincipal_ReceivesZeroRecordsOverMcpAndRest()
    {
        // The layer is populated for somebody: five rows are readable by the role
        // the policy admits, so the denied reads below cannot pass vacuously.
        using var reader = CreateClient(ReaderRole);
        (await ReadOverRestAsync(reader, AllowedLayerId)).Should().HaveCount(5);

        using var denied = CreateClient(UnrelatedRole);

        var features = await CallQueryFeaturesAsync(denied, AllowedLayerId);
        AssertZeroRecords(features, AllowedLayerMarker);

        var countOnly = await CallQueryFeaturesAsync(denied, AllowedLayerId, returnCountOnly: true);
        AssertZeroRecords(countOnly, AllowedLayerMarker);
        countOnly.GetProperty("structuredContent").TryGetProperty("count", out _).Should().BeFalse(
            "a denied principal must not learn the layer's cardinality either");

        // The REST half of the parity: same principal, same refusal, no records.
        using var restDenied = await denied.GetAsync(QueryUrl(AllowedLayerId, "1=1"));
        var restBody = await restDenied.Content.ReadAsStringAsync();
        AssertGeoServicesForbidden(restBody);
        restBody.Should().NotContain(AllowedLayerMarker);
        JsonDocument.Parse(restBody).RootElement.TryGetProperty("features", out _).Should().BeFalse();
    }

    /// <summary>
    /// GeoServices reports errors in an Esri error envelope on HTTP 200; the refusal is
    /// the <c>error.code</c>, and no record may ride alongside it.
    /// </summary>
    private static void AssertGeoServicesForbidden(string body)
    {
        using var document = JsonDocument.Parse(body);
        document.RootElement.TryGetProperty("error", out var error).Should().BeTrue(body);
        error.GetProperty("code").GetInt32().Should().Be(403, body);
        document.RootElement.TryGetProperty("features", out _).Should().BeFalse(
            "a refused REST read must carry no feature records");
    }

    /// <summary>
    /// Asserts a refused MCP tool result carries no feature records in any of the
    /// three places records can ride: the MCP projection, the GeoJSON collection,
    /// and the raw serialized envelope.
    /// </summary>
    private static void AssertZeroRecords(JsonElement result, string rowMarker)
    {
        result.GetProperty("isError").GetBoolean().Should().BeTrue();
        var structured = result.GetProperty("structuredContent");
        structured.GetProperty("code").GetString().Should().Be("permission_denied");

        if (structured.TryGetProperty("features", out var features))
        {
            features.GetArrayLength().Should().Be(0, "a denial must carry zero feature records");
        }

        if (structured.TryGetProperty("geojson", out var geoJson)
            && geoJson.ValueKind == JsonValueKind.Object
            && geoJson.TryGetProperty("features", out var geoJsonFeatures))
        {
            geoJsonFeatures.GetArrayLength().Should().Be(0, "a denial must carry zero GeoJSON records");
        }

        result.GetRawText().Should().NotContain(rowMarker,
            "no stored attribute value may appear anywhere in a refused response");
    }

    private async Task<IReadOnlyList<FeatureRecord>> ReadOverRestAsync(
        HttpClient client,
        int layerId,
        string where = "1=1")
    {
        using var response = await client.GetAsync(QueryUrl(layerId, where));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, body);

        using var document = JsonDocument.Parse(body);
        var records = new List<FeatureRecord>();
        foreach (var feature in document.RootElement.GetProperty("features").EnumerateArray())
        {
            var attributes = feature.GetProperty("attributes");
            double? x = null;
            double? y = null;
            if (feature.TryGetProperty("geometry", out var geometry)
                && geometry.ValueKind == JsonValueKind.Object
                && geometry.TryGetProperty("x", out var xValue)
                && geometry.TryGetProperty("y", out var yValue))
            {
                x = Round(xValue.GetDouble());
                y = Round(yValue.GetDouble());
            }

            records.Add(new FeatureRecord(
                attributes.GetProperty("objectid").GetInt64(),
                ReadString(attributes, "name"),
                ReadString(attributes, "description"),
                ReadString(attributes, "category"),
                x,
                y));
        }

        return [.. records.OrderBy(record => record.ObjectId)];
    }

    private async Task<IReadOnlyList<FeatureRecord>> ReadOverMcpAsync(
        HttpClient client,
        int layerId,
        string? where = null)
    {
        var result = await CallQueryFeaturesAsync(client, layerId, where: where);
        result.GetProperty("isError").GetBoolean().Should().BeFalse(result.GetRawText());

        var records = new List<FeatureRecord>();
        foreach (var feature in result.GetProperty("structuredContent").GetProperty("features").EnumerateArray())
        {
            var attributes = feature.GetProperty("attributes");
            double? x = null;
            double? y = null;
            if (feature.TryGetProperty("geometry", out var geometry)
                && geometry.ValueKind == JsonValueKind.Object
                && geometry.TryGetProperty("coordinates", out var coordinates)
                && coordinates.GetArrayLength() >= 2)
            {
                x = Round(coordinates[0].GetDouble());
                y = Round(coordinates[1].GetDouble());
            }

            records.Add(new FeatureRecord(
                attributes.GetProperty("objectid").GetInt64(),
                ReadString(attributes, "name"),
                ReadString(attributes, "description"),
                ReadString(attributes, "category"),
                x,
                y));
        }

        return [.. records.OrderBy(record => record.ObjectId)];
    }

    private async Task<JsonElement> CallQueryFeaturesAsync(
        HttpClient client,
        int layerId,
        string? where = null,
        bool returnCountOnly = false)
    {
        var whereArgument = where is null
            ? string.Empty
            : $",\"where\":{JsonSerializer.Serialize(where)}";
        var arguments = "{\"serviceId\":\"" + ServiceId
            + "\",\"layerId\":" + layerId.ToString(CultureInfo.InvariantCulture)
            + ",\"limit\":1000,\"returnCountOnly\":" + (returnCountOnly ? "true" : "false")
            + whereArgument + "}";
        var body = "{\"jsonrpc\":\"2.0\",\"id\":\"feature-read-parity\",\"method\":\"tools/call\","
            + "\"params\":{\"name\":\"honua_query_features\",\"arguments\":" + arguments + "}}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(body, Encoding.UTF8)
            {
                Headers = { ContentType = new MediaTypeHeaderValue("application/json") },
            },
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request);
        var payload = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, payload);

        using var document = JsonDocument.Parse(payload);
        document.RootElement.TryGetProperty("error", out var protocolError).Should().BeFalse(
            "honua_query_features should return a tool result, not a JSON-RPC error: {0}",
            protocolError.ValueKind == JsonValueKind.Undefined ? "(none)" : protocolError.GetRawText());
        return document.RootElement.GetProperty("result").Clone();
    }

    private static string QueryUrl(int layerId, string where)
        => $"/rest/services/{ServiceId}/FeatureServer/{layerId}/query"
            + $"?where={Uri.EscapeDataString(where)}&outFields=*&returnGeometry=true&orderByFields=objectid&f=json";

    private static string? ReadString(JsonElement attributes, string name)
        => attributes.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double Round(double value) => Math.Round(value, 6, MidpointRounding.AwayFromZero);

    private HttpClient CreateClient(string role) => _fixture.CreateClient(client =>
    {
        client.DefaultRequestHeaders.Add(ParityAuthHandler.UserHeader, $"parity-{role}");
        client.DefaultRequestHeaders.Add(ParityAuthHandler.RolesHeader, role);
    });

    /// <summary>One comparable record, surface-independent.</summary>
    private sealed record FeatureRecord(
        long ObjectId,
        string? Name,
        string? Description,
        string? Category,
        double? X,
        double? Y);

    /// <summary>
    /// Admits the generic MCP operator gate so the per-layer resource policy is the
    /// only authorization decision this class measures.
    /// </summary>
    private sealed class AdmitEveryOperatorRequest : IOperatorAuthorizationEvaluator
    {
        public Task<AccessDecision> EvaluateAsync(
            ClaimsPrincipal principal,
            OperatorAuthorizationRequest request,
            CancellationToken cancellationToken = default)
            => Task.FromResult(AccessDecision.Allowed("feature-read parity fixture"));
    }

    /// <summary>
    /// Minimal authenticated identity carrying a stable subject plus the roles the
    /// access policies name. Only the identity is synthesized; every authorization
    /// decision below it is the production one.
    /// </summary>
    private sealed class ParityAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string SchemeName = "FeatureReadParity";
        public const string UserHeader = "X-Parity-User";
        public const string RolesHeader = "X-Parity-Roles";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var users)
                || users.FirstOrDefault() is not { Length: > 0 } user)
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, user),
                new(ClaimTypes.NameIdentifier, user),
                new("sub", user),
                new("tenant_id", "default"),
            };

            if (Request.Headers.TryGetValue(RolesHeader, out var roles))
            {
                foreach (var role in roles.ToString()
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                    claims.Add(new Claim("roles", role));
                }
            }

            var identity = new ClaimsIdentity(claims, Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(
                new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
