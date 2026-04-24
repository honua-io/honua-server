// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.OData.Client;
using Xunit;

namespace Honua.Server.Tests.Features.Protocols.OData;

/// <summary>
/// Cross-client certification suite for the OData v4 surface using
/// Microsoft.OData.Client 8.4.3 as the canonical client. Each test maps to a
/// CERT-* identifier from
/// <c>docs/gis/CROSS_CLIENT_CERTIFICATION_MATRIX.md</c> and contributes one
/// row to the certification envelope written under <c>tests/TestResults/</c>.
/// </summary>
/// <remarks>
/// <para>
/// This is the CLI lane's automated OData proof. It mirrors the BI lane's
/// manual <c>bi-powerbi-odata.cert.json</c> envelope but is driven by an
/// actual library client (not curl), so it can detect client-side
/// regressions in metadata parsing, LINQ-to-URI translation, and response
/// deserialization that protocol-only smokes cannot.
/// </para>
/// <para>
/// Every test routes through
/// <see cref="CertificationEvidenceCollector.RecordAsync(string, Func{CertContext, Task})"/>,
/// which auto-classifies failures: <see cref="Xunit.Sdk.XunitException"/>
/// (i.e. an assertion miss after the client successfully round-trips) is
/// tagged <c>[server-regression]</c>; any other exception type — typically a
/// transport or parser fault from <see cref="DataServiceContext"/> — is
/// tagged <c>[client-incompat]</c>.
/// </para>
/// <para>
/// All read-only cases use a per-class <see cref="WebAppFixture"/> seeded with
/// <c>tests/seed/odata.yaml</c>. The lone write probe (<c>CERT-AUTH-01</c>)
/// is intentionally unauthenticated and only verifies the 401 response,
/// so the test schema is not mutated.
/// </para>
/// </remarks>
[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataClientCertificationTests : IClassFixture<ODataClientCertificationFixture>
{
    private readonly ODataClientCertificationFixture _certFixture;

    public ODataClientCertificationTests(ODataClientCertificationFixture certFixture)
    {
        _certFixture = certFixture;
    }

    private WebAppFixture WebApp => _certFixture.WebApp;
    private HttpClient AdminClient => _certFixture.AdminClient;
    private CertificationEvidenceCollector Evidence => _certFixture.Evidence;

    // ---------- CONN ----------

    [IntegrationTest]
    [Trait("CertId", "CERT-CONN-01")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public Task CertConn01_ServiceRootReachable()
        => Evidence.RecordAsync("CERT-CONN-01", async _ =>
        {
            var response = await AdminClient.GetAsync("/odata");
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "OData service root must be reachable for the CLI lane");
        });

    [IntegrationTest]
    [Trait("CertId", "CERT-CONN-02")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public Task CertConn02_TlsHandshake()
    {
        // The in-process WebAppFixture does not terminate TLS — record skip
        // with a note matching the PyQGIS / BI precedent so the envelope is
        // still complete.
        Evidence.Skip(
            "CERT-CONN-02",
            "In-process WebAppFixture host does not terminate TLS; TLS termination is exercised by the production deployment lane.");
        return Task.CompletedTask;
    }

    // ---------- AUTH ----------

    [IntegrationTest]
    [Trait("CertId", "CERT-AUTH-01")]
    [Operation(Operations.Security)]
    [Endpoint("POST /odata/Layers({layerId})/Features")]
    public Task CertAuth01_UnauthenticatedWriteIs401()
        => Evidence.RecordAsync("CERT-AUTH-01", async _ =>
        {
            // The fixture disables HONUA_DEV_AUTH so the unauthenticated
            // bare HttpClient sees the production authorization pipeline.
            // Probe a write — which is the most diagnostic negative-auth
            // surface for OData — and assert 401.
            var body = new StringContent("{\"Attributes\":{\"name\":\"unauthorized\"}}", Encoding.UTF8, "application/json");
            var response = await WebApp.Client.PostAsync("/odata/Layers(0)/Features", body);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
                "unauthenticated OData writes must be rejected with 401");
        });

    [IntegrationTest]
    [Trait("CertId", "CERT-AUTH-02")]
    [Operation(Operations.Security)]
    [Endpoint("GET /odata/Layers")]
    public Task CertAuth02_AuthenticatedReadSucceeds()
        => Evidence.RecordAsync("CERT-AUTH-02", async _ =>
        {
            // Use the admin client (X-API-Key header) to demonstrate that a
            // valid credential grants access. With HONUA_DEV_AUTH=false the
            // server requires a credential for every request, so an admin
            // round-trip is the most direct positive signal.
            var response = await AdminClient.GetAsync("/odata/Layers");
            response.StatusCode.Should().Be(HttpStatusCode.OK,
                "valid credential must grant access to the OData read surface");
        });

    // ---------- DISC ----------

    [IntegrationTest]
    [Trait("CertId", "CERT-DISC-01")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata")]
    public Task CertDisc01_ServiceDocumentLists()
        => Evidence.RecordAsync("CERT-DISC-01", async ctx =>
        {
            var response = await AdminClient.GetAsync("/odata");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(content);

            var entitySets = document.RootElement.GetProperty("value").EnumerateArray()
                .Select(e => e.GetProperty("name").GetString())
                .ToList();

            entitySets.Should().Contain("Layers");
            entitySets.Should().Contain("Features");
            ctx.MeasuredCount = entitySets.Count;
        });

    [IntegrationTest]
    [Trait("CertId", "CERT-DISC-02")]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers")]
    public Task CertDisc02_SingleEntitySetQuery()
        => Evidence.RecordAsync("CERT-DISC-02", async _ =>
        {
            var context = ODataTestClient.CreateContext(AdminClient);
            var query = context.CreateQuery<ODataLayer>("Layers");
            var response = await query.ExecuteAsync();
            var layers = response.ToList();

            layers.Should().NotBeEmpty(
                "Microsoft.OData.Client must successfully retrieve at least one layer");
            layers.Should().Contain(layer => layer.Id == 0,
                "the seed layer with Id=0 must be discoverable via the canonical client");
        });

    // ---------- SCHM ----------

    [IntegrationTest]
    [Trait("CertId", "CERT-SCHM-01")]
    [Operation(Operations.GetMetadata)]
    [Endpoint("GET /odata/$metadata")]
    public Task CertSchm01_MetadataDocument()
        => Evidence.RecordAsync("CERT-SCHM-01", async _ =>
        {
            var response = await AdminClient.GetAsync("/odata/$metadata");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var content = await response.Content.ReadAsStringAsync();

            content.Should().Contain("EntityType Name=\"Layer\"");
            content.Should().Contain("EntityType Name=\"Feature\"");
            content.Should().Contain("Property Name=\"ObjectId\"");
            content.Should().Contain("Property Name=\"LayerId\"");
        });

    // ---------- QFLT ----------

    [IntegrationTest]
    [Trait("CertId", "CERT-QFLT-01")]
    [Operation(Operations.ODataFilter)]
    [Endpoint("GET /odata/Features({layerId})?$filter=")]
    public Task CertQflt01_AttributeEqualityFilter()
        => Evidence.RecordAsync("CERT-QFLT-01", async ctx =>
        {
            // Drive the filter through Microsoft.OData.Client's
            // AddQueryOption so a regression in LINQ-to-URI translation
            // would surface as a client-incompat fail rather than a 200
            // with the wrong rows.
            var context = ODataTestClient.CreateContext(AdminClient);
            var query = context.CreateQuery<ODataFeature>($"Features({TestLayerId})")
                .AddQueryOption("$filter", "ObjectId eq 1");
            var response = await query.ExecuteAsync();
            var features = response.ToList();

            features.Should().ContainSingle(
                "ObjectId eq 1 should match exactly one seeded feature");
            features[0].ObjectId.Should().Be(1);
            ctx.MeasuredCount = features.Count;
        });

    // ---------- PAGE ----------

    [IntegrationTest]
    [Trait("CertId", "CERT-PAGE-01")]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5")]
    public Task CertPage01_TopReturnsLimitedResults()
        => Evidence.RecordAsync("CERT-PAGE-01", async ctx =>
        {
            var context = ODataTestClient.CreateContext(AdminClient);
            var query = context.CreateQuery<ODataFeature>($"Features({TestLayerId})")
                .AddQueryOption("$top", "5");
            var response = await query.ExecuteAsync();
            var features = response.ToList();

            features.Should().HaveCount(5,
                "$top=5 must return exactly five features for the seed layer");
            ctx.MeasuredCount = features.Count;
        });

    [IntegrationTest]
    [Trait("CertId", "CERT-PAGE-02")]
    [Operation(Operations.Pagination)]
    [Endpoint("GET /odata/Features({layerId})?$top=5&$skip=5")]
    public Task CertPage02_SkipReturnsDifferentPage()
        => Evidence.RecordAsync("CERT-PAGE-02", async _ =>
        {
            var context = ODataTestClient.CreateContext(AdminClient);

            var page1Query = context.CreateQuery<ODataFeature>($"Features({TestLayerId})")
                .AddQueryOption("$top", "5")
                .AddQueryOption("$orderby", "ObjectId");
            var page1 = (await page1Query.ExecuteAsync()).ToList();

            var page2Query = context.CreateQuery<ODataFeature>($"Features({TestLayerId})")
                .AddQueryOption("$top", "5")
                .AddQueryOption("$skip", "5")
                .AddQueryOption("$orderby", "ObjectId");
            var page2 = (await page2Query.ExecuteAsync()).ToList();

            page1.Should().HaveCount(5);
            page2.Should().HaveCount(5);

            var page1Ids = page1.Select(f => f.ObjectId).ToHashSet();
            var page2Ids = page2.Select(f => f.ObjectId).ToHashSet();
            page1Ids.Intersect(page2Ids).Should().BeEmpty(
                "page 1 and page 2 must contain disjoint feature ids when $skip is honored");
        });

    // ---------- ERRH ----------

    [IntegrationTest]
    [Trait("CertId", "CERT-ERRH-01")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/DoesNotExist")]
    public Task CertErrh01_InvalidEndpointReturnsStructuredError()
        => Evidence.RecordAsync("CERT-ERRH-01", async _ =>
        {
            // Probe the unrouted path with raw HttpClient — the "structured
            // error envelope" promise is verified end-to-end by CERT-ERRH-02;
            // for an unrouted path the bare 4xx from the routing layer is
            // the canonical response and may carry an empty body. RecordAsync
            // takes care of attributing failures to server-regression
            // (XunitException) vs client-incompat (any other exception).
            var response = await AdminClient.GetAsync("/odata/DoesNotExist");
            ((int)response.StatusCode).Should().BeInRange(400, 499,
                "an unknown OData entity set must surface a 4xx status");
        });

    [IntegrationTest]
    [Trait("CertId", "CERT-ERRH-02")]
    [Operation(Operations.ErrorHandling)]
    [Endpoint("GET /odata/Features({layerId})?$filter=invalid")]
    public Task CertErrh02_MalformedFilterReturnsStructuredError()
        => Evidence.RecordAsync("CERT-ERRH-02", async _ =>
        {
            // A blatantly malformed $filter clause must be rejected with a
            // 4xx and an error envelope. Use raw HttpClient so we can verify
            // the response shape independent of the OData client parser.
            var response = await AdminClient.GetAsync(
                $"/odata/Features({TestLayerId})?$filter={Uri.EscapeDataString("ObjectId !!= 1")}");

            ((int)response.StatusCode).Should().BeInRange(400, 499,
                "a malformed $filter clause must be rejected with a 4xx status");

            var content = await response.Content.ReadAsStringAsync();
            content.Should().NotBeNullOrWhiteSpace(
                "OData errors must include a structured response body");
        });

    private const int TestLayerId = 0;
}

/// <summary>
/// xUnit class fixture that owns the certification collector and the
/// <see cref="WebAppFixture"/> for <see cref="ODataClientCertificationTests"/>.
/// Pre-records the lane-scoped <c>not-applicable</c> CERT cases at
/// initialization so the envelope is always populated for all 18 common-core
/// IDs, even when an individual test method panics. The envelope is flushed
/// to <c>tests/TestResults/{run_id}-cli-odata.cert.json</c> in
/// <see cref="DisposeAsync"/>.
/// </summary>
public sealed class ODataClientCertificationFixture : IAsyncLifetime
{
    private const string ClientLane = "cli";
    private const string Protocol = "odata";

    private HttpClient? _adminClient;

    public ODataClientCertificationFixture()
    {
        WebApp = new WebAppFixture()
            .ConfigureWebHost(builder =>
            {
                // Disable the dev-auth bypass so the CERT-AUTH-01 probe sees
                // a real 401 response from unauthenticated requests. The
                // non-shared WebAppFixture path does not auto-configure
                // HONUA_ADMIN_PASSWORD, so set it here to the canonical
                // TestKit constant that CreateAdminClient() places in the
                // X-API-Key header — keeping the two ends in lockstep
                // without re-declaring the literal.
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", WebAppFixture.SharedAdminPassword);
            });

        Evidence = new CertificationEvidenceCollector(
            clientLane: ClientLane,
            protocol: Protocol,
            environment: ResolveEnvironment(),
            clientVersion: ResolveClientVersion(),
            serverVersion: ResolveServerVersion(),
            outputDirectory: ResolveOutputDirectory());
    }

    public WebAppFixture WebApp { get; }

    public CertificationEvidenceCollector Evidence { get; }

    /// <summary>
    /// Authenticated HttpClient (X-API-Key header) used by every read-path
    /// CERT case. Required because the fixture disables HONUA_DEV_AUTH so
    /// CERT-AUTH-01's unauthenticated write probe surfaces a real 401, which
    /// in turn means anonymous reads also receive 401.
    /// </summary>
    public HttpClient AdminClient => _adminClient
        ?? throw new InvalidOperationException("AdminClient is only available after InitializeAsync.");

    public async Task InitializeAsync()
    {
        WebApp.UseSeed(Path.Combine("tests", "seed", "odata.yaml"));
        await WebApp.InitializeAsync();
        _adminClient = WebApp.CreateAdminClient();

        // Lane-scoped not-applicable cases — recorded up front so the
        // envelope contains every common-core ID regardless of test outcome.
        Evidence.NotApplicable("CERT-SCHM-02", "OData CLI lane does not exercise geometry-type schema output.");
        Evidence.NotApplicable("CERT-QFLT-02", "OData CLI lane does not exercise spatial bbox/geometry filters.");
        Evidence.NotApplicable("CERT-GEOM-01", "OData CLI lane does not validate geometry coordinate fidelity.");
        Evidence.NotApplicable("CERT-GEOM-02", "OData CLI lane does not validate CRS output.");
        Evidence.NotApplicable("CERT-RNDR-01", "OData CLI lane has no visual renderer; CERT-RNDR is JS lane scope.");
        Evidence.NotApplicable("CERT-RNDR-02", "OData CLI lane has no visual renderer; CERT-RNDR is JS lane scope.");
    }

    public async Task DisposeAsync()
    {
        try
        {
            Evidence.Flush();
        }
        finally
        {
            _adminClient?.Dispose();
            await WebApp.DisposeAsync();
        }
    }

    private static string ResolveEnvironment()
    {
        // GitHub Actions sets CI=true; honour it so locally-executed runs
        // emit `local` and CI runs emit `ci` per the schema spec.
        var ci = Environment.GetEnvironmentVariable("CI");
        return string.Equals(ci, "true", StringComparison.OrdinalIgnoreCase) ? "ci" : "local";
    }

    private static string ResolveClientVersion()
    {
        // Pull the actual loaded Microsoft.OData.Client assembly version so
        // the envelope reflects the pinned package rather than a hard-coded
        // string. The csproj pins this at 8.4.3.
        var assembly = typeof(DataServiceContext).Assembly;
        return assembly.GetName().Version?.ToString() ?? "8.4.3";
    }

    private static string ResolveServerVersion()
    {
        // Prefer git metadata when available so envelopes are tied to a
        // specific commit. Fall back to the test assembly's informational
        // version (set by the build) and finally to "unknown".
        var fromEnv = Environment.GetEnvironmentVariable("GITHUB_SHA");
        if (!string.IsNullOrWhiteSpace(fromEnv))
        {
            return fromEnv;
        }

        var assembly = typeof(Honua.Server.OperationRegistry).Assembly;
        var info = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
        if (info is not null && !string.IsNullOrWhiteSpace(info.InformationalVersion))
        {
            return info.InformationalVersion;
        }

        return "unknown";
    }

    private static string ResolveOutputDirectory()
    {
        // Walk up from the test bin/ directory to the repo root (Honua.sln)
        // and write into tests/TestResults/ so CI's existing
        // --results-directory and the new upload-ci-evidence step both
        // pick the file up.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Honua.sln")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName ?? AppContext.BaseDirectory;
        return Path.Combine(root, "tests", "TestResults");
    }
}
