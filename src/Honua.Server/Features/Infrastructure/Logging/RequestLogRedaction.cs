// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Honua.Infrastructure.Logging;

internal static class RequestLogConfiguration
{
    internal static LoggerConfiguration ConfigureHonuaRequestDiagnostics(this LoggerConfiguration configuration) =>
        configuration.MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Information)
            // Enrichment runs before every sink, including UseSerilog's forwarded providers.
            .Enrich.With(new RequestLogRedaction());
}

/// <summary>
/// Removes request credentials at the logging boundary without mutating the live request.
/// </summary>
internal sealed partial class RequestLogRedaction : ILogEventEnricher
{
    private const string Redacted = "[REDACTED]";

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var property in logEvent.Properties.ToArray())
        {
            logEvent.AddOrUpdateProperty(new LogEventProperty(property.Key, Redact(property.Key, property.Value)));
        }
    }

    private static LogEventPropertyValue Redact(string name, LogEventPropertyValue value)
    {
        // Hosting emits QueryString separately from Path. Suppress the entire value:
        // feature filters can contain private data even when their parameter names are safe.
        // Header collections are also private by default; individual safe diagnostic
        // properties (path, protocol, correlation IDs) remain available.
        if (value is ScalarValue { Value: "" })
        {
            return value;
        }

        if (IsCredentialName(name) || name.Contains("QueryString", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith("Headers", StringComparison.OrdinalIgnoreCase))
        {
            return new ScalarValue(Redacted);
        }

        return value switch
        {
            ScalarValue { Value: string text } => new ScalarValue(ScrubCredentialParameters(text)),
            SequenceValue sequence => new SequenceValue(sequence.Elements.Select(element => Redact(string.Empty, element))),
            StructureValue structure => new StructureValue(structure.Properties.Select(property =>
                new LogEventProperty(property.Name, Redact(property.Name, property.Value))), structure.TypeTag),
            DictionaryValue dictionary => new DictionaryValue(dictionary.Elements.Select(element =>
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(element.Key,
                    Redact(element.Key.Value as string ?? string.Empty, element.Value)))),
            _ => value
        };
    }

    private static bool IsCredentialName(string name) =>
        name.Contains("key", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("credential", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("cookie", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("sig", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("pwd", StringComparison.OrdinalIgnoreCase);

    private static string ScrubCredentialParameters(string text)
    {
        try
        {
            return QueryParameter().Replace(text, match =>
                IsCredentialName(Uri.UnescapeDataString(match.Groups[2].Value))
                    ? string.Concat(match.Groups[1].Value, match.Groups[2].Value, "=", Redacted)
                    : match.Value);
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed on adversarial diagnostic values.
            return Redacted;
        }
    }

    [GeneratedRegex(@"([?&])([^?&=\s]+)=([^&\s]*)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex QueryParameter();
}
