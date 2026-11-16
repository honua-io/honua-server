// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Honua.ServiceDefaults;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
[Collection("Performance")]
public sealed class SloMetricContractTests : IClassFixture<TestWebApplicationFactory>
{
    private const string AdminPassword = "slo-metric-contract-admin-key";

    private static readonly string[] UnitMangledRegressionNames =
    [
        "honua_geoservices_error_total_errors_total",
        "honua_request_error_total_errors_total",
        "honua_serving_request_duration_ms_milliseconds_count",
    ];

    private readonly WebApplicationFactory<Program> _factory;

    public SloMetricContractTests(TestWebApplicationFactory factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /metrics")]
    public async Task PrometheusExposition_ExportsEverySeriesNamedByTheSloMetricContract()
    {
        var contract = LoadContract();
        var exposition = await ScrapeAfterEmittingContractInstrumentsAsync();
        var exported = ParseSeriesNames(exposition);

        foreach (var expected in contract.Emitted)
        {
            exported.Should().Contain(
                expected.Series,
                "honua-helm / honua-devops / honua-release alert on '{0}'; eng/telemetry/slo-metric-contract.v1.json "
                + "declares the server emits it. If this name changed, EVERY SLO ratio using it silently "
                + "evaluates to an empty vector and stops firing. Update the consumers and the contract "
                + "together, or restore the name.",
                expected.Series);
        }
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /metrics")]
    public async Task PrometheusExposition_DoesNotReintroduceTheUnitSuffixedSeriesNames()
    {
        var exposition = await ScrapeAfterEmittingContractInstrumentsAsync();
        var exported = ParseSeriesNames(exposition);

        foreach (var mangled in UnitMangledRegressionNames)
        {
            exported.Should().NotContain(
                mangled,
                "'{0}' means an OpenTelemetry unit was re-declared on an SLO instrument. The OTel Prometheus "
                + "exporter appends the UCUM-mapped unit to the metric name, which renames the series out from "
                + "under every consumer without breaking a single build. See the SLO-contract comment in "
                + "HonuaTelemetry.cs.",
                mangled);
        }
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /metrics")]
    public async Task PrometheusExposition_CarriesTheLabelNamesTheContractPromises()
    {
        var contract = LoadContract();
        var exposition = await ScrapeAfterEmittingContractInstrumentsAsync();

        foreach (var expected in contract.Emitted)
        {
            var labels = ParseLabelNames(exposition, expected.Series);
            labels.Should().NotBeEmpty("'{0}' must appear in the exposition", expected.Series);

            foreach (var label in expected.Labels)
            {
                labels.Should().Contain(
                    label,
                    "alert rules select and group '{0}' by '{1}'. Note that dotted instrument tag keys are "
                    + "sanitized by the exporter (honua.protocol -> honua_protocol), so the PromQL label name is "
                    + "NOT the .NET tag constant.",
                    expected.Series,
                    label);
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.TestInfrastructure)]
    [Endpoint("GET /metrics")]
    public async Task ServingDenominator_ExcludesHealthProbesAndTheMetricsScrape()
    {
        // The SLO denominator must cover the same population as its numerator. Probe traffic is not
        // serving-plane traffic and it dwarfs real traffic — a candidate idling for 24h with 10s
        // liveness+readiness probes and a 15s Prometheus scrape accrues ~23k samples. If those land
        // in the denominator, a smoke run of 200 GeoServices calls with 20 errors (a real 10% error
        // rate) reads as 0.00087 and passes a 1% error budget, so the release gate becomes
        // FAIL-OPEN — strictly worse than the no-signal state it replaced (honua-release#5).
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        (await client.GetAsync("/healthz/live")).EnsureSuccessStatusCode();

        for (var i = 0; i < 5; i++)
        {
            await client.GetAsync("/healthz/live");
            await client.GetAsync("/healthz/ready");
            await client.GetAsync("/metrics");
        }

        var exposition = await client.GetStringAsync("/metrics");
        var probeProtocols = ParseLabelValues(
            exposition, "honua_serving_request_duration_ms_count", "honua_protocol");

        probeProtocols.Should().NotContain(
            HonuaTelemetry.Protocols.Health,
            "health probes are infrastructure traffic, not serving-plane requests, and counting them "
            + "in the SLO denominator dilutes any real error rate toward zero");
        probeProtocols.Should().NotContain(
            HonuaTelemetry.Protocols.Monitoring,
            "the Prometheus scrape must not inflate the denominator of the budget it is scraped for");
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Contract_GeoServicesSelector_MatchesTheProtocolFamilyInCode()
    {
        // The contract publishes the GeoServices scope as a PromQL selector for out-of-repo alert
        // rules and the release gate. Code is the source of truth; this fails if they drift.
        var selector = ContractSelector("geoservices_protocols");

        foreach (var protocol in HonuaTelemetry.Protocols.GeoServicesFamily)
        {
            selector.Should().Contain(
                protocol,
                "the published selector must cover every GeoServices protocol the classifier emits, "
                + "or those requests vanish from the denominator");
        }

        var published = selector.Split(['"'], StringSplitOptions.RemoveEmptyEntries)[1].Split('|');
        published.Should().BeEquivalentTo(
            HonuaTelemetry.Protocols.GeoServicesFamily,
            "a selector member with no matching protocol constant is dead weight that hides a rename");
    }

    private async Task<string> ScrapeAfterEmittingContractInstrumentsAsync()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        // The host — and with it the OpenTelemetry MeterProvider that subscribes to the "Honua"
        // meter — is built lazily by the factory, and OTel only aggregates measurements taken after
        // the provider starts. Force the host up before recording, or the samples are dropped and
        // this guard fails for a reason that has nothing to do with metric naming.
        (await client.GetAsync("/healthz/live")).EnsureSuccessStatusCode();

        // in-band (HTTP 200 with an {error} body) — the keystone GeoServices signal.
        HonuaTelemetry.RecordErrorEnvelope("FeatureServer", "query", errorCode: 200, isGeoServices: true);
        // transport error on a non-GeoServices surface.
        HonuaTelemetry.RecordErrorEnvelope("OGC", "getFeatures", errorCode: 500, isGeoServices: false);
        HonuaTelemetry.RecordServingRequest(
            HonuaTelemetry.Protocols.FeatureServer, "query", statusCode: 200, durationMs: 12.5);

        var response = await client.GetAsync("/metrics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await response.Content.ReadAsStringAsync();
    }

    private static SloMetricContract LoadContract()
    {
        var path = RepositoryPaths.Resolve("eng", "telemetry", "slo-metric-contract.v1.json");
        File.Exists(path).Should().BeTrue("the SLO metric contract is the shared source of truth for {0}", path);

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var emitted = document.RootElement.GetProperty("emitted").EnumerateArray()
            .Select(entry => new SloMetricContractEntry(
                entry.GetProperty("series").GetString() ?? string.Empty,
                [.. entry.GetProperty("labels").EnumerateArray().Select(label => label.GetString() ?? string.Empty)]))
            .ToArray();

        emitted.Should().NotBeEmpty("an empty contract would make this guard vacuously green");
        return new SloMetricContract(emitted);
    }

    private static HashSet<string> ParseSeriesNames(string exposition)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        // codeql[cs/linq/missed-select]: skips blank and comment lines while collecting series names
        foreach (var raw in exposition.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var cut = line.IndexOfAny(['{', ' ']);
            if (cut > 0)
            {
                names.Add(line[..cut]);
            }
        }

        return names;
    }

    private static string ContractSelector(string name)
    {
        var path = RepositoryPaths.Resolve("eng", "telemetry", "slo-metric-contract.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.GetProperty("selectors").GetProperty(name).GetString()
            ?? string.Empty;
    }

    private static HashSet<string> ParseLabelValues(string exposition, string series, string label)
    {
        var values = new HashSet<string>(StringComparer.Ordinal);
        var pattern = new Regex(
            "^" + Regex.Escape(series) + @"\{(?<labels>[^}]*)\}", RegexOptions.CultureInvariant);

        // codeql[cs/linq/missed-select]: collects values of one label from each matching exposition line
        foreach (var raw in exposition.Split('\n'))
        {
            var match = pattern.Match(raw.Trim());
            if (!match.Success)
            {
                continue;
            }

            foreach (var pair in match.Groups["labels"].Value.Split(','))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0 && pair[..equals].Trim() == label)
                {
                    values.Add(pair[(equals + 1)..].Trim().Trim('"'));
                }
            }
        }

        return values;
    }

    private static HashSet<string> ParseLabelNames(string exposition, string series)
    {
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var pattern = new Regex("^" + Regex.Escape(series) + @"\{(?<labels>[^}]*)\}", RegexOptions.CultureInvariant);

        // codeql[cs/linq/missed-select]: parses label names from each matching exposition line
        foreach (var raw in exposition.Split('\n'))
        {
            var match = pattern.Match(raw.Trim());
            if (!match.Success)
            {
                continue;
            }

            foreach (var pair in match.Groups["labels"].Value.Split(','))
            {
                var equals = pair.IndexOf('=', StringComparison.Ordinal);
                if (equals > 0)
                {
                    labels.Add(pair[..equals].Trim());
                }
            }
        }

        return labels;
    }

    private sealed record SloMetricContract(IReadOnlyList<SloMetricContractEntry> Emitted);

    private sealed record SloMetricContractEntry(string Series, IReadOnlyList<string> Labels);
}
