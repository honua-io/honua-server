// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Amazon;
using Amazon.CloudWatch;
using Amazon.CloudWatch.Model;

namespace Honua.ControlPlane;

/// <summary>
/// Thin seam over the AWS CloudWatch <c>GetMetricData</c> API so the deploy telemetry gate can be
/// unit-tested without a live AWS account. The production implementation is
/// <see cref="AwsSdkCloudWatchMetricClient"/>; tests substitute a fake.
/// </summary>
internal interface ICloudWatchMetricClient
{
    /// <summary>
    /// Evaluates a single CloudWatch metric-math expression over the supplied window and returns
    /// the most recent datapoint, or <c>null</c> when CloudWatch returned no datapoints.
    /// </summary>
    Task<double?> GetExpressionValueAsync(
        string? region,
        string expression,
        DateTime startUtc,
        DateTime endUtc,
        int periodSeconds,
        CancellationToken cancellationToken);
}

internal sealed class AwsSdkCloudWatchMetricClient : ICloudWatchMetricClient
{
    public async Task<double?> GetExpressionValueAsync(
        string? region,
        string expression,
        DateTime startUtc,
        DateTime endUtc,
        int periodSeconds,
        CancellationToken cancellationToken)
    {
        using var client = CreateClient(region);
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

        var result = response.MetricDataResults?.FirstOrDefault();
        if (result?.Values is { Count: > 0 } values)
        {
            // ScanBy.TimestampDescending orders datapoints newest-first.
            return values[0];
        }

        return null;
    }

    private static AmazonCloudWatchClient CreateClient(string? region)
        => string.IsNullOrWhiteSpace(region)
            ? new AmazonCloudWatchClient()
            : new AmazonCloudWatchClient(RegionEndpoint.GetBySystemName(region));
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
                "telemetry.error_rate.query / telemetry.latency_p95.query / telemetry.sample_count.query CloudWatch metric-math expressions.");
        }

        var region = ResolveRegion(connection);
        var endUtc = DateTime.UtcNow;
        var startUtc = endUtc.AddSeconds(-WindowSeconds);

        var sampleCount = string.IsNullOrWhiteSpace(policy.MinimumSampleQuery)
            ? (double?)null
            : await metricClient.GetExpressionValueAsync(region, policy.MinimumSampleQuery, startUtc, endUtc, WindowSeconds, cancellationToken).ConfigureAwait(false);

        if (policy.MinimumSampleCount.HasValue && (!sampleCount.HasValue || sampleCount.Value < policy.MinimumSampleCount.Value))
        {
            return new DeployTelemetryReadings { SampleCount = sampleCount };
        }

        double? errorRate = null;
        if (!string.IsNullOrWhiteSpace(policy.ErrorRateQuery) && policy.ErrorRateThreshold.HasValue)
        {
            errorRate = await metricClient.GetExpressionValueAsync(region, policy.ErrorRateQuery, startUtc, endUtc, WindowSeconds, cancellationToken).ConfigureAwait(false);
        }

        double? latencyP95 = null;
        if (!string.IsNullOrWhiteSpace(policy.LatencyP95Query) && policy.LatencyP95ThresholdMs.HasValue)
        {
            latencyP95 = await metricClient.GetExpressionValueAsync(region, policy.LatencyP95Query, startUtc, endUtc, WindowSeconds, cancellationToken).ConfigureAwait(false);
        }

        return new DeployTelemetryReadings
        {
            SampleCount = sampleCount,
            ErrorRate = errorRate,
            LatencyP95 = latencyP95
        };
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
