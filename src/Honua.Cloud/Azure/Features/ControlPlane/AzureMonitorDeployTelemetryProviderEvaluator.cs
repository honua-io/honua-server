// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Xml;
using Azure.Core;
using Azure.Identity;

namespace Honua.ControlPlane;

/// <summary>
/// One Log Analytics scalar read. <see cref="NoRows"/> is set only when the query succeeded and its
/// first table has no rows, which is what an error-count query with a <c>by</c> or <c>bin()</c>
/// clause returns for an error-free window. A failed request, a missing table, an empty row or an
/// unparseable or non-finite cell is never <see cref="NoRows"/>.
/// </summary>
internal readonly record struct AzureMonitorScalarReading(double? Value, bool NoRows)
{
    /// <summary>A successful query whose result table has no rows.</summary>
    public static AzureMonitorScalarReading Empty { get; } = new(null, NoRows: true);

    /// <summary>A read that produced no usable value and no proof that the result is empty.</summary>
    public static AzureMonitorScalarReading Unavailable { get; } = new(null, NoRows: false);
}

/// <summary>
/// Thin seam over the Azure Monitor Logs (Log Analytics / Application Insights) query API so the
/// deploy telemetry gate can be unit-tested without a live Azure subscription. The production
/// implementation is <see cref="AzureMonitorLogsQueryClient"/>; tests substitute a fake. The seam
/// mirrors <c>ICloudWatchMetricClient</c> on the AWS side: a single scalar reading per configured
/// signal evaluated over a fixed window.
/// </summary>
internal interface IAzureMonitorMetricClient
{
    /// <summary>
    /// Evaluates a single Kusto (KQL) query against the supplied Log Analytics workspace over the
    /// supplied window and returns the first scalar cell of the first result row, or no value
    /// when the workspace returned no rows.
    /// </summary>
    Task<AzureMonitorScalarReading> GetScalarReadingAsync(
        string workspaceId,
        string query,
        TimeSpan window,
        string? endpointOverride = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Azure Monitor Logs query client using direct HTTPS calls to the Log Analytics query API
/// (<c>https://api.loganalytics.io</c> by default), authenticated with
/// <see cref="DefaultAzureCredential"/>. Direct-REST is the same approach the sibling Azure deploy
/// adapters (<c>AzureManagementContainerAppsRevisionClient</c>,
/// <c>AzureManagementFunctionsSlotClient</c>) take against <c>management.azure.com</c>, so the
/// Azure surface does not pull the heavier Azure.Monitor.Query SDK transitively.
/// </summary>
internal sealed class AzureMonitorLogsQueryClient(IHttpClientFactory httpClientFactory)
    : IAzureMonitorMetricClient
{
    // Public-cloud Log Analytics query endpoint. Sovereign clouds (for example
    // https://api.loganalytics.azure.cn or https://api.loganalytics.us) supply an endpoint override
    // on the telemetry connection's BaseUrl.
    internal const string DefaultEndpoint = "https://api.loganalytics.io";

    private readonly TokenCredential _credential = AzureControlPlaneCredential.SharedDefault;

    public async Task<AzureMonitorScalarReading> GetScalarReadingAsync(
        string workspaceId,
        string query,
        TimeSpan window,
        string? endpointOverride = null,
        CancellationToken cancellationToken = default)
    {
        var endpoint = ResolveEndpoint(endpointOverride);
        var requestUri = new Uri(endpoint, $"v1/workspaces/{Uri.EscapeDataString(workspaceId)}/query");

        var payload = BuildQueryPayload(query, window);
        using var request = await CreateRequestAsync(endpoint, requestUri, payload, cancellationToken).ConfigureAwait(false);

        using var client = httpClientFactory.CreateClient("control-plane-azure");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        await EnsureSuccessAsync(response, cancellationToken).ConfigureAwait(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        return ParseScalarReading(document.RootElement);
    }

    private async Task<HttpRequestMessage> CreateRequestAsync(
        Uri endpoint,
        Uri requestUri,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        // The token resource follows the resolved endpoint authority so sovereign-cloud endpoints
        // sign with the matching audience (https://{host}/.default).
        var scope = $"https://{endpoint.Host}/.default";

        AccessToken accessToken;
        try
        {
            accessToken = await _credential.GetTokenAsync(
                    new TokenRequestContext([scope]),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AuthenticationFailedException ex)
        {
            // Normalize credential failures to a null-status HttpRequestException so they flow
            // through the same recoverable path as transport failures (mirrors the Container Apps /
            // Functions ARM clients): the backend preserves durable rollout state on an ambiguous
            // outcome instead of terminalizing on a transient managed-identity IMDS hiccup.
            throw new HttpRequestException(
                "Azure Monitor Logs query credential acquisition failed.",
                ex);
        }

        var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken.Token);
        request.Content = new ByteArrayContent(payload);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = Encoding.UTF8.WebName
        };

        return request;
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"Azure Monitor Logs query failed with status {(int)response.StatusCode}: {body}",
            null,
            response.StatusCode);
    }

    private static byte[] BuildQueryPayload(string query, TimeSpan window)
    {
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);
        writer.WriteStartObject();
        writer.WriteString("query", query);
        // ISO 8601 duration (for example "PT5M") restricts the query to the rollout's bake window,
        // matching the CloudWatch evaluator's fixed-window metric-math evaluation.
        writer.WriteString("timespan", XmlConvert.ToString(window));
        writer.WriteEndObject();
        writer.Flush();

        return stream.ToArray();
    }

    // The Log Analytics query response shape is { "tables": [ { "columns": [...],
    // "rows": [ [ <cell>, ... ], ... ] } ] }. A health/error/latency gate query is expected to
    // project a single scalar; read tables[0].rows[0][0] and coerce it to a double.
    internal static double? ParseScalar(JsonElement root)
        => ParseScalarReading(root).Value;

    /// <summary>
    /// Reads the scalar and classifies an empty answer: only a present first table whose
    /// <c>rows</c> array is empty is <see cref="AzureMonitorScalarReading.NoRows"/>.
    /// </summary>
    internal static AzureMonitorScalarReading ParseScalarReading(JsonElement root)
    {
        if (!root.TryGetProperty("tables", out var tables) ||
            tables.ValueKind != JsonValueKind.Array ||
            tables.GetArrayLength() == 0)
        {
            return AzureMonitorScalarReading.Unavailable;
        }

        var table = tables[0];
        if (!table.TryGetProperty("rows", out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
        {
            return AzureMonitorScalarReading.Unavailable;
        }

        // A partial result is not the whole answer, so an empty table in it proves nothing.
        if (rows.GetArrayLength() == 0)
        {
            return root.TryGetProperty("error", out _)
                ? AzureMonitorScalarReading.Unavailable
                : AzureMonitorScalarReading.Empty;
        }

        // A health/error/latency gate query is expected to project exactly one row (a scalar). More
        // than one is an ambiguous-cardinality configuration error (#4617): silently reading rows[0]
        // can promote or hold a deploy on an arbitrary, unintended row.
        if (rows.GetArrayLength() > 1)
        {
            throw new InvalidOperationException(
                $"Telemetry query resolved to {rows.GetArrayLength()} rows; expected exactly one scalar row. " +
                "Aggregate the query (for example with summarize) to a single value.");
        }

        var firstRow = rows[0];
        if (firstRow.ValueKind != JsonValueKind.Array || firstRow.GetArrayLength() == 0)
        {
            return AzureMonitorScalarReading.Unavailable;
        }

        var cell = firstRow[0];
        var parsedValue = cell.ValueKind switch
        {
            JsonValueKind.Number => cell.GetDouble(),
            JsonValueKind.String when double.TryParse(
                cell.GetString(),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => (double?)null
        };

        // Guard the same non-finite class as every other provider (#4617): an undefined Log
        // Analytics expression must not silently satisfy a threshold comparison.
        return parsedValue.HasValue && double.IsFinite(parsedValue.Value)
            ? new AzureMonitorScalarReading(parsedValue.Value, NoRows: false)
            : AzureMonitorScalarReading.Unavailable;
    }

    private static Uri ResolveEndpoint(string? endpointOverride)
    {
        if (!string.IsNullOrWhiteSpace(endpointOverride) &&
            Uri.TryCreate(endpointOverride.Trim(), UriKind.Absolute, out var overrideUri))
        {
            return overrideUri;
        }

        return new Uri(DefaultEndpoint);
    }
}

/// <summary>
/// Azure Monitor-backed deploy telemetry provider. Selected when a telemetry connection's
/// <c>Provider</c> is <c>azuremonitor</c>. The policy's error-rate / latency / sample-count query
/// strings are interpreted as Log Analytics / Application Insights KQL queries that each project a
/// single scalar. Thresholds and the promote/rollback/wait decision are shared with every other
/// provider via <c>DeployTelemetrySignalEvaluator</c>, so only the query dialect differs from the
/// Prometheus and CloudWatch evaluators — giving Azure deploy backends the same health-gated safe
/// rollout + auto-rollback parity as the AWS backends.
/// </summary>
internal sealed class AzureMonitorDeployTelemetryProviderEvaluator(
    IAzureMonitorMetricClient metricClient) : IDeployTelemetryProviderEvaluator
{
    // Mirror the Prometheus presets' 5m rate window and the CloudWatch evaluator's 300s sample
    // horizon so thresholds keep the same meaning across providers.
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(300);

    public string Provider => "azuremonitor";

    public async Task<DeployTelemetryReadings> ReadAsync(
        DeployTelemetryPolicyDescriptor policy,
        DeployTelemetryConnectionDescriptor connection,
        CancellationToken cancellationToken)
    {
        // Azure Monitor has no preset query dialect; the Honua HTTP presets emit PromQL. Require the
        // operator to supply explicit KQL queries so we never silently ship PromQL to Log Analytics
        // and stall the gate.
        if (!policy.HasExplicitQueryOverride)
        {
            throw new InvalidOperationException(
                $"Telemetry connection '{connection.ConnectionId}' uses the azuremonitor provider, which requires explicit " +
                "telemetry.error_rate.query (or telemetry.error_count.query) / telemetry.latency_p95.query / " +
                "telemetry.sample_count.query KQL queries.");
        }

        // The Log Analytics workspace id is carried on the connection's Region field, mirroring how
        // the CloudWatch evaluator repurposes Region for the AWS region. BaseUrl, when non-default,
        // overrides the query endpoint for sovereign clouds.
        var workspaceId = ResolveWorkspaceId(connection);
        if (string.IsNullOrWhiteSpace(workspaceId))
        {
            throw new InvalidOperationException(
                $"Telemetry connection '{connection.ConnectionId}' uses the azuremonitor provider, which requires the " +
                "Log Analytics workspace id to be supplied via the connection's Region setting.");
        }

        var endpointOverride = ResolveEndpointOverride(connection);

        // Every signal reads the same window, so an error count and the sample count it is divided
        // by describe the same traffic.
        var sampleCount = string.IsNullOrWhiteSpace(policy.MinimumSampleQuery)
            ? (double?)null
            : (await metricClient.GetScalarReadingAsync(workspaceId, policy.MinimumSampleQuery, Window, endpointOverride, cancellationToken).ConfigureAwait(false)).Value;

        // Short-circuit when the sample-count gate already fails; mirrors the Prometheus/CloudWatch
        // evaluators that do not read error-rate/latency until the minimum sample gate clears.
        if (policy.MinimumSampleCount.HasValue && (!sampleCount.HasValue || sampleCount.Value < policy.MinimumSampleCount.Value))
        {
            return new DeployTelemetryReadings { SampleCount = sampleCount };
        }

        double? errorRate = null;
        if (!string.IsNullOrWhiteSpace(policy.ErrorRateQuery) && policy.ErrorRateThreshold.HasValue)
        {
            errorRate = (await metricClient.GetScalarReadingAsync(workspaceId, policy.ErrorRateQuery, Window, endpointOverride, cancellationToken).ConfigureAwait(false)).Value;
        }

        // Same bounded rule as the CloudWatch provider (honua-server#6474): only an error-count
        // numerator keeps the "no rows" outcome for the shared gate to read as zero errors.
        var errorCount = AzureMonitorScalarReading.Unavailable;
        if (!string.IsNullOrWhiteSpace(policy.ErrorCountQuery) && policy.ErrorRateThreshold.HasValue)
        {
            errorCount = await metricClient.GetScalarReadingAsync(workspaceId, policy.ErrorCountQuery, Window, endpointOverride, cancellationToken).ConfigureAwait(false);
        }

        double? latencyP95 = null;
        if (!string.IsNullOrWhiteSpace(policy.LatencyP95Query) && policy.LatencyP95ThresholdMs.HasValue)
        {
            latencyP95 = (await metricClient.GetScalarReadingAsync(workspaceId, policy.LatencyP95Query, Window, endpointOverride, cancellationToken).ConfigureAwait(false)).Value;
        }

        return new DeployTelemetryReadings
        {
            SampleCount = sampleCount,
            ErrorRate = errorRate,
            ErrorCount = errorCount.Value,
            ErrorCountSeriesAbsent = errorCount.NoRows,
            LatencyP95 = latencyP95
        };
    }

    private static string? ResolveWorkspaceId(DeployTelemetryConnectionDescriptor connection)
        => string.IsNullOrWhiteSpace(connection.Region) ? null : connection.Region.Trim();

    // A non-default BaseUrl selects a sovereign-cloud Log Analytics endpoint; the standard public
    // endpoint (or an unset BaseUrl) leaves the client on its built-in default.
    private static string? ResolveEndpointOverride(DeployTelemetryConnectionDescriptor connection)
    {
        if (string.IsNullOrWhiteSpace(connection.BaseUrl) ||
            !Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var isStandardPublicEndpoint = string.Equals(
            uri.Host,
            new Uri(AzureMonitorLogsQueryClient.DefaultEndpoint).Host,
            StringComparison.OrdinalIgnoreCase);

        return isStandardPublicEndpoint ? null : connection.BaseUrl.Trim();
    }
}
