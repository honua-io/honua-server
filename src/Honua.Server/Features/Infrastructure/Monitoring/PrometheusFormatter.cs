using System.Globalization;
using System.Text;

namespace Honua.Server.Features.Infrastructure.Monitoring;

/// <summary>
/// Formats metrics in Prometheus exposition format for monitoring integrations.
/// </summary>
/// <remarks>
/// This class provides a simple way to expose application metrics in the Prometheus
/// text-based exposition format, enabling integration with Prometheus monitoring systems.
/// </remarks>
internal sealed class PrometheusFormatter
{
    private readonly StringBuilder _builder = new();
    private readonly HashSet<string> _addedMetrics = new();

    /// <summary>
    /// Adds a gauge metric (point-in-time measurement).
    /// </summary>
    /// <param name="name">Metric name</param>
    /// <param name="value">Metric value</param>
    /// <param name="help">Help text describing the metric</param>
    /// <param name="labels">Optional label key-value pairs</param>
    public void AddGauge(string name, double value, string help, params string[] labels)
    {
        AddMetric("gauge", name, value, help, labels);
    }

    /// <summary>
    /// Adds a counter metric (monotonically increasing value).
    /// </summary>
    /// <param name="name">Metric name</param>
    /// <param name="value">Metric value</param>
    /// <param name="help">Help text describing the metric</param>
    /// <param name="labels">Optional label key-value pairs</param>
    public void AddCounter(string name, double value, string help, params string[] labels)
    {
        AddMetric("counter", name, value, help, labels);
    }

    /// <summary>
    /// Adds a histogram metric (distribution of measurements).
    /// </summary>
    /// <param name="name">Metric name</param>
    /// <param name="value">Metric value</param>
    /// <param name="help">Help text describing the metric</param>
    /// <param name="labels">Optional label key-value pairs</param>
    public void AddHistogram(string name, double value, string help, params string[] labels)
    {
        AddMetric("histogram", name, value, help, labels);
    }

    /// <summary>
    /// Returns the formatted metrics string.
    /// </summary>
    /// <returns>Prometheus-formatted metrics text</returns>
    public override string ToString()
    {
        return _builder.ToString();
    }

    /// <summary>
    /// Adds a metric with the specified type.
    /// </summary>
    private void AddMetric(string type, string name, double value, string help, string[] labels)
    {
        var normalizedName = NormalizeMetricName(name);
        var metricKey = $"{normalizedName}_{type}";

        // Add HELP and TYPE comments only once per metric
        if (!_addedMetrics.Contains(metricKey))
        {
            _builder.AppendLine($"# HELP {normalizedName} {SanitizeHelp(help)}");
            _builder.AppendLine($"# TYPE {normalizedName} {type}");
            _addedMetrics.Add(metricKey);
        }

        // Build the metric line
        _builder.Append(normalizedName);

        // Add labels if present
        if (labels.Length > 0 && labels.Length % 2 == 0)
        {
            _builder.Append('{');
            for (int i = 0; i < labels.Length; i += 2)
            {
                if (i > 0) _builder.Append(',');
                _builder.Append($"{NormalizeLabelName(labels[i])}=\"{EscapeLabelValue(labels[i + 1])}\"");
            }
            _builder.Append('}');
        }

        // Add the value
        _builder.Append(' ');
        _builder.Append(FormatValue(value));

        // Add timestamp (current Unix time in milliseconds)
        _builder.Append(' ');
        _builder.AppendLine(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString());
    }

    /// <summary>
    /// Normalizes metric names to conform to Prometheus naming conventions.
    /// </summary>
    private static string NormalizeMetricName(string name)
    {
        if (string.IsNullOrEmpty(name))
            throw new ArgumentException("Metric name cannot be null or empty", nameof(name));

        var normalized = new StringBuilder();
        bool firstChar = true;

        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsLetter(c) || (!firstChar && char.IsDigit(c)))
            {
                normalized.Append(c);
                firstChar = false;
            }
            else if (!firstChar && (c == '_' || c == ':'))
            {
                normalized.Append(c);
            }
            else if (!firstChar)
            {
                normalized.Append('_');
            }
        }

        var result = normalized.ToString();
        return string.IsNullOrEmpty(result) ? "metric" : result;
    }

    /// <summary>
    /// Normalizes label names to conform to Prometheus naming conventions.
    /// </summary>
    private static string NormalizeLabelName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return "label";

        var normalized = new StringBuilder();
        bool firstChar = true;

        foreach (char c in name.ToLowerInvariant())
        {
            if (char.IsLetter(c) || (!firstChar && char.IsDigit(c)))
            {
                normalized.Append(c);
                firstChar = false;
            }
            else if (!firstChar && c == '_')
            {
                normalized.Append(c);
            }
            else if (!firstChar)
            {
                normalized.Append('_');
            }
        }

        var result = normalized.ToString();
        return string.IsNullOrEmpty(result) ? "label" : result;
    }

    /// <summary>
    /// Escapes label values for Prometheus format.
    /// </summary>
    private static string EscapeLabelValue(string value)
    {
        if (string.IsNullOrEmpty(value))
            return "";

        return value
            .Replace("\\", "\\\\")  // Escape backslashes
            .Replace("\"", "\\\"")  // Escape quotes
            .Replace("\n", "\\n")   // Escape newlines
            .Replace("\t", "\\t")   // Escape tabs
            .Replace("\r", "\\r");  // Escape carriage returns
    }

    /// <summary>
    /// Sanitizes help text to remove potentially problematic characters.
    /// </summary>
    private static string SanitizeHelp(string help)
    {
        if (string.IsNullOrEmpty(help))
            return "No description provided";

        return help
            .Replace('\n', ' ')
            .Replace('\r', ' ')
            .Replace('\t', ' ')
            .Trim();
    }

    /// <summary>
    /// Formats numeric values for Prometheus (handles NaN and infinity).
    /// </summary>
    private static string FormatValue(double value)
    {
        if (double.IsNaN(value))
            return "NaN";
        if (double.IsPositiveInfinity(value))
            return "+Inf";
        if (double.IsNegativeInfinity(value))
            return "-Inf";

        return value.ToString("G17", CultureInfo.InvariantCulture);
    }
}