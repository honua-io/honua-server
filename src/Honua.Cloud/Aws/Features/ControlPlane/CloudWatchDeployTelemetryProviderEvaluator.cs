// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;
using Honua.Cloud.Aws.Features;

namespace Honua.ControlPlane;

/// <summary>
/// One CloudWatch signal read: the newest finite datapoint, or none. <see cref="SeriesAbsent"/> is
/// set only when <c>GetMetricData</c> completed and the expression matched no series or returned no
/// datapoints, which is how CloudWatch reports an error-count metric (ALB
/// <c>HTTPCode_Target_5XX_Count</c>, Lambda <c>Errors</c>) for an error-free window. A partial or
/// failed result, or a non-finite datapoint, is never <see cref="SeriesAbsent"/>.
/// </summary>
internal readonly record struct CloudWatchSignalReading(double? Value, bool SeriesAbsent)
{
    /// <summary>A completed read that matched no series or returned no datapoints.</summary>
    public static CloudWatchSignalReading Absent { get; } = new(null, SeriesAbsent: true);

    /// <summary>A read that produced no usable value and no proof that the series is empty.</summary>
    public static CloudWatchSignalReading Unavailable { get; } = new(null, SeriesAbsent: false);
}

/// <summary>
/// Thin seam over the AWS CloudWatch <c>GetMetricData</c> API so the deploy telemetry gate can be
/// unit-tested without a live AWS account. The production implementation is
/// <see cref="AwsSdkCloudWatchMetricClient"/>; tests substitute a fake.
/// </summary>
internal interface ICloudWatchMetricClient
{
    /// <summary>
    /// Evaluates a single CloudWatch metric-math expression over the supplied window and returns
    /// the most recent datapoint, or no value when CloudWatch returned no datapoints.
    /// </summary>
    Task<CloudWatchSignalReading> GetExpressionReadingAsync(
        string? region,
        string expression,
        DateTime startUtc,
        DateTime endUtc,
        int periodSeconds,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default);
}

internal sealed class AwsSdkCloudWatchMetricClient : ICloudWatchMetricClient
{
    public async Task<CloudWatchSignalReading> GetExpressionReadingAsync(
        string? region,
        string expression,
        DateTime startUtc,
        DateTime endUtc,
        int periodSeconds,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region, serviceUrl);
        var response = await client.GetMetricDataAsync(
            new GetMetricDataRequest
            {
                StartTime = startUtc,
                EndTime = endUtc,
                ScanBy = ScanBy.TimestampDescending,
                MetricDataQueries =
                [
                    new MetricDataQuery
                    {
                        Id = "honua_deploy_signal",
                        Expression = expression,
                        Period = periodSeconds,
                        ReturnData = true
                    }
                ]
            },
            cancellationToken).ConfigureAwait(false);

        return SelectSignalReading(response);
    }

    /// <summary>
    /// Reads the single signal value out of a <c>GetMetricData</c> response. Visible for testing.
    /// </summary>
    internal static double? SelectSignalValue(GetMetricDataResponse response)
        => SelectSignalReading(response).Value;

    /// <summary>
    /// Reads the single signal out of a <c>GetMetricData</c> response and classifies an empty answer.
    /// Visible for testing.
    /// </summary>
    internal static CloudWatchSignalReading SelectSignalReading(GetMetricDataResponse response)
    {
        // More data pages mean this page is not the whole answer; never read it as an empty series.
        if (!string.IsNullOrEmpty(response.NextToken))
        {
            return CloudWatchSignalReading.Unavailable;
        }

        var results = response.MetricDataResults;
        if (results is not { Count: > 0 })
        {
            // A SEARCH that matches no metric (no error-count metric was published for the window)
            // can come back with no result for the query at all.
            return CloudWatchSignalReading.Absent;
        }

        // The request carries one query, so more than one result means the expression resolved to
        // several time series (a SEARCH or a GROUP BY Metrics Insights query). Reading the first one
        // would promote or hold a deploy on an arbitrary series (#4617), exactly as for a Prometheus
        // vector or a Log Analytics table with several rows.
        if (results.Count > 1)
        {
            throw new InvalidOperationException(
                $"Telemetry query resolved to {results.Count} series; expected exactly one. " +
                "Aggregate the expression to a single time series.");
        }

        var result = results[0];
        if (result.Values is { Count: > 0 } values)
        {
            // ScanBy.TimestampDescending orders datapoints newest-first. Metric-math over an
            // undefined ratio (for example 0/0) can yield NaN/Infinity; treat that the same as no
            // datapoint rather than a real reading that could silently satisfy a threshold (#4617).
            return double.IsFinite(values[0])
                ? new CloudWatchSignalReading(values[0], SeriesAbsent: false)
                : CloudWatchSignalReading.Unavailable;
        }

        // No datapoints is an empty series only when CloudWatch reports the result complete. A
        // PartialData, InternalError or Forbidden status is a failed read, not proof of zero.
        return result.StatusCode is null || result.StatusCode == StatusCode.Complete
            ? CloudWatchSignalReading.Absent
            : CloudWatchSignalReading.Unavailable;
    }

    // Visible for testing. Applies an opt-in, config-driven ServiceURL override (for example a
    // LocalStack Community CloudWatch endpoint) when supplied; when unset the client uses the
    // default regional endpoint, keeping production behaviour unchanged.
    internal static AmazonCloudWatchClient CreateClient(string? region, string? serviceUrl)
    {
        var config = new AmazonCloudWatchConfig();

        // An explicit endpoint takes precedence for the request URL while the region (when set)
        // stays the SigV4 signing region. Unset = default regional endpoint.
        AwsClientEndpoint.Apply(config, region, serviceUrl);

        return new AmazonCloudWatchClient(config);
    }
}

/// <summary>
/// CloudWatch-backed deploy telemetry provider. Selected when a telemetry connection's
/// <c>Provider</c> is <c>cloudwatch</c>. The policy's error-rate / latency / sample-count query
/// strings are interpreted as CloudWatch metric-math expressions (for example
/// <c>SELECT AVG(...)</c> Metrics Insights or arithmetic over metric ids). Thresholds and the
/// promote/rollback/wait decision are shared with every other provider via
/// <c>DeployTelemetryProviderEvaluation</c>, so only the query dialect differs from Prometheus.
/// </summary>
internal sealed class CloudWatchDeployTelemetryProviderEvaluator(
    ICloudWatchMetricClient metricClient) : IDeployTelemetryProviderEvaluator
{
    // CloudWatch metric-math evaluates over a window; mirror the Prometheus presets' 5m rate
    // window and 300s sample horizon so thresholds keep the same meaning across providers.
    private const int WindowSeconds = 300;

    public string Provider => "cloudwatch";

    public async Task<DeployTelemetryReadings> ReadAsync(
        DeployTelemetryPolicyDescriptor policy,
        DeployTelemetryConnectionDescriptor connection,
        CancellationToken cancellationToken)
    {
        // CloudWatch has no preset query dialect; the Honua HTTP presets emit PromQL. Require the
        // operator to supply explicit CloudWatch metric-math expressions so we never silently ship
        // PromQL to CloudWatch and stall.
        if (!policy.HasExplicitQueryOverride)
        {
            throw new InvalidOperationException(
                $"Telemetry connection '{connection.ConnectionId}' uses the cloudwatch provider, which requires explicit " +
                "telemetry.error_rate.query (or telemetry.error_count.query) / telemetry.latency_p95.query / " +
                "telemetry.sample_count.query CloudWatch metric-math expressions.");
        }

        var region = ResolveRegion(connection);
        var serviceUrl = ResolveServiceUrl(connection);
        var endUtc = DateTime.UtcNow;
        var startUtc = endUtc.AddSeconds(-WindowSeconds);

        // Every signal reads the same [startUtc, endUtc) window, so an error count and the sample
        // count it is divided by describe the same traffic.
        var sampleCount = string.IsNullOrWhiteSpace(policy.MinimumSampleQuery)
            ? (double?)null
            : (await metricClient.GetExpressionReadingAsync(region, policy.MinimumSampleQuery, startUtc, endUtc, WindowSeconds, serviceUrl, cancellationToken).ConfigureAwait(false)).Value;

        if (policy.MinimumSampleCount.HasValue && (!sampleCount.HasValue || sampleCount.Value < policy.MinimumSampleCount.Value))
        {
            return new DeployTelemetryReadings { SampleCount = sampleCount };
        }

        double? errorRate = null;
        if (!string.IsNullOrWhiteSpace(policy.ErrorRateQuery) && policy.ErrorRateThreshold.HasValue)
        {
            errorRate = (await metricClient.GetExpressionReadingAsync(region, policy.ErrorRateQuery, startUtc, endUtc, WindowSeconds, serviceUrl, cancellationToken).ConfigureAwait(false)).Value;
        }

        // An error-count numerator (honua-server#6474) keeps the "matched no series" outcome so the
        // shared gate can read it as zero errors against a floor-meeting sample count. An empty
        // error-RATE result stays missing evidence: a ratio cannot tell no errors from no traffic.
        var errorCount = CloudWatchSignalReading.Unavailable;
        if (!string.IsNullOrWhiteSpace(policy.ErrorCountQuery) && policy.ErrorRateThreshold.HasValue)
        {
            errorCount = await metricClient.GetExpressionReadingAsync(region, policy.ErrorCountQuery, startUtc, endUtc, WindowSeconds, serviceUrl, cancellationToken).ConfigureAwait(false);
        }

        double? latencyP95 = null;
        if (!string.IsNullOrWhiteSpace(policy.LatencyP95Query) && policy.LatencyP95ThresholdMs.HasValue)
        {
            latencyP95 = (await metricClient.GetExpressionReadingAsync(region, policy.LatencyP95Query, startUtc, endUtc, WindowSeconds, serviceUrl, cancellationToken).ConfigureAwait(false)).Value;
        }

        return new DeployTelemetryReadings
        {
            SampleCount = sampleCount,
            ErrorRate = errorRate,
            ErrorCount = errorCount.Value,
            ErrorCountSeriesAbsent = errorCount.SeriesAbsent,
            LatencyP95 = latencyP95
        };
    }

    // Opt-in, config-driven endpoint override. A custom CloudWatch BaseUrl (for example a
    // LocalStack Community endpoint such as http://localhost:4566) is forwarded to the SDK as a
    // ServiceURL so the telemetry gate can be exercised against an emulator. Standard regional
    // monitoring endpoints are left to ResolveRegion / the SDK default endpoint, so production
    // connections that only set BaseUrl for region inference keep their existing behaviour.
    private static string? ResolveServiceUrl(DeployTelemetryConnectionDescriptor connection)
    {
        if (string.IsNullOrWhiteSpace(connection.BaseUrl) ||
            !Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var labels = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var isStandardRegionalEndpoint =
            labels.Length >= 4 &&
            string.Equals(labels[0], "monitoring", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(labels[^2], "amazonaws", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(labels[^1], "com", StringComparison.OrdinalIgnoreCase);

        return isStandardRegionalEndpoint ? null : connection.BaseUrl.Trim();
    }

    private static string? ResolveRegion(DeployTelemetryConnectionDescriptor connection)
    {
        if (!string.IsNullOrWhiteSpace(connection.Region))
        {
            return connection.Region.Trim();
        }

        // Fall back to inferring the region from a regional CloudWatch BaseUrl such as
        // https://monitoring.us-east-1.amazonaws.com. When neither is set the SDK's default
        // credential/region resolution chain applies.
        if (Uri.TryCreate(connection.BaseUrl, UriKind.Absolute, out var uri))
        {
            var labels = uri.Host.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Length >= 4 &&
                string.Equals(labels[0], "monitoring", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(labels[^2], "amazonaws", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(labels[^1], "com", StringComparison.OrdinalIgnoreCase))
            {
                return labels[1];
            }
        }

        return null;
    }
}
