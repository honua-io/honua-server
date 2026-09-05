// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

/// <summary>
/// Opt-in deployed-environment proof for the evidence-posture outage gate (#3475). The harness owns
/// the two control URLs; Honua never exposes a production outage-control route. Both controls must be
/// idempotent and return success only after the selected telemetry backend reaches the requested state.
/// </summary>
public sealed class EvidencePostureLiveTests
{
    private const string BaseUrlEnv = "HONUA_LIVE_EVIDENCE_BASE_URL";
    private const string ApiKeyEnv = "HONUA_LIVE_EVIDENCE_API_KEY";
    private const string SourceIdEnv = "HONUA_LIVE_EVIDENCE_SOURCE_ID";
    private const string FindingIdEnv = "HONUA_LIVE_EVIDENCE_FINDING_ID";
    private const string OutageUrlEnv = "HONUA_LIVE_EVIDENCE_OUTAGE_URL";
    private const string RecoveryUrlEnv = "HONUA_LIVE_EVIDENCE_RECOVERY_URL";
    private static readonly TimeSpan TransitionTimeout = TimeSpan.FromMinutes(2);

    [CloudTest(BaseUrlEnv, ApiKeyEnv, SourceIdEnv, FindingIdEnv, OutageUrlEnv, RecoveryUrlEnv)]
    public async Task TelemetryBackendOutage_McpStaysReachableAndProposalFailsClosedUntilRecovery()
    {
        var sourceId = Required(SourceIdEnv);
        var findingId = Required(FindingIdEnv);
        using var honua = CreateAuthenticatedClient(RequiredUri(BaseUrlEnv));
        using var controls = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var initial = await ReadFindingsViaMcpAsync(honua, findingId);
        AssertCompleteAndFresh(initial, sourceId);
        AssertFindingPresent(initial, findingId);

        var outageRequested = false;
        try
        {
            outageRequested = true;
            var outageRequestedAt = DateTimeOffset.UtcNow;
            await InvokeControlAsync(controls, RequiredUri(OutageUrlEnv));

            var unavailable = await WaitForSourceAsync(
                honua,
                findingId,
                sourceId,
                completeness: "unavailable");
            unavailable.GetProperty("evidencePosture").GetProperty("status").GetString()
                .Should().Be("unavailable");
            var unavailableSource = FindSource(unavailable, sourceId);
            unavailableSource.GetProperty("observedAt").GetDateTimeOffset().Should().BeOnOrBefore(outageRequestedAt);
            unavailableSource.GetProperty("lastSuccessfulAt").GetDateTimeOffset().Should().BeOnOrBefore(outageRequestedAt);
            unavailableSource.GetProperty("reasonCodes").EnumerateArray()
                .Select(reason => reason.GetString())
                .Should().Contain("sourceUnavailable");

            using var proposal = await honua.PostAsync(
                $"api/v1/admin/observability/findings/{Uri.EscapeDataString(findingId)}/propose",
                content: null);
            proposal.StatusCode.Should().Be(HttpStatusCode.OK);
            using var proposalJson = JsonDocument.Parse(await proposal.Content.ReadAsStringAsync());
            proposalJson.RootElement.GetProperty("status").GetString().Should().Be("Blocked");
            proposalJson.RootElement.GetProperty("message").GetString()
                .Should().Be("evidencePostureNotActionable");
        }
        finally
        {
            if (outageRequested)
            {
                await InvokeControlAsync(controls, RequiredUri(RecoveryUrlEnv));
            }
        }

        var recovered = await WaitForSourceAsync(honua, findingId, sourceId, completeness: "complete");
        AssertCompleteAndFresh(recovered, sourceId);
        AssertFindingPresent(recovered, findingId);
    }

    private static HttpClient CreateAuthenticatedClient(Uri? baseAddress)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.BaseAddress = baseAddress;
        client.DefaultRequestHeaders.Add("X-API-Key", Required(ApiKeyEnv));
        return client;
    }

    private static async Task<JsonElement> WaitForSourceAsync(
        HttpClient client,
        string findingId,
        string sourceId,
        string completeness)
    {
        using var timeout = new CancellationTokenSource(TransitionTimeout);
        while (true)
        {
            var findings = await ReadFindingsViaMcpAsync(client, findingId, timeout.Token);
            var source = FindSource(findings, sourceId);
            if (string.Equals(
                    source.GetProperty("completeness").GetString(),
                    completeness,
                    StringComparison.Ordinal))
            {
                return findings;
            }

            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);
        }
    }

    private static async Task<JsonElement> ReadFindingsViaMcpAsync(
        HttpClient client,
        string findingId,
        CancellationToken cancellationToken = default)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id = "evidence-posture-live",
            method = "tools/call",
            @params = new
            {
                name = "honua_ops_findings",
                arguments = new { findingId },
            },
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, "mcp")
        {
            Content = new StringContent(requestBody, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await client.SendAsync(request, cancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, "MCP must remain reachable during telemetry failure");
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
        document.RootElement.TryGetProperty("error", out _).Should().BeFalse();
        return document.RootElement
            .GetProperty("result")
            .GetProperty("structuredContent")
            .Clone();
    }

    private static async Task InvokeControlAsync(HttpClient client, Uri controlUri)
    {
        using var response = await client.PostAsync(controlUri, content: null);
        response.IsSuccessStatusCode.Should().BeTrue("the live harness control must complete successfully");
    }

    private static void AssertCompleteAndFresh(JsonElement findings, string sourceId)
    {
        findings.GetProperty("evidencePosture").GetProperty("status").GetString().Should().Be("complete");
        var source = FindSource(findings, sourceId);
        source.GetProperty("completeness").GetString().Should().Be("complete");
        source.GetProperty("backendKind").GetString().Should().NotBe("unverified");
        source.GetProperty("backendId").GetString().Should().NotBeNullOrWhiteSpace();
        var now = DateTimeOffset.UtcNow;
        var oldestValid = now.AddSeconds(-source.GetProperty("maximumAgeSeconds").GetInt64());
        source.GetProperty("observedAt").GetDateTimeOffset().Should().BeOnOrAfter(oldestValid).And.BeOnOrBefore(now);
        source.GetProperty("lastSuccessfulAt").GetDateTimeOffset().Should().BeOnOrAfter(oldestValid).And.BeOnOrBefore(now);
        source.GetProperty("validUntil").GetDateTimeOffset().Should().BeAfter(DateTimeOffset.UtcNow);
    }

    private static JsonElement FindSource(JsonElement findings, string sourceId) =>
        findings.GetProperty("evidencePosture").GetProperty("sources").EnumerateArray()
            .Single(source => string.Equals(
                source.GetProperty("sourceId").GetString(),
                sourceId,
                StringComparison.Ordinal));

    private static void AssertFindingPresent(JsonElement findings, string findingId) =>
        findings.GetProperty("findings").EnumerateArray()
            .Should().Contain(finding => string.Equals(
                finding.GetProperty("id").GetString(),
                findingId,
                StringComparison.Ordinal));

    private static Uri RequiredUri(string envName)
    {
        var value = Required(envName);
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new InvalidOperationException($"{envName} must be an absolute HTTP(S) URI.");
        }

        return uri;
    }

    private static string Required(string envName)
    {
        var value = Environment.GetEnvironmentVariable(envName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"Live evidence validation requires {envName}.");
        }

        return value.Trim();
    }
}
