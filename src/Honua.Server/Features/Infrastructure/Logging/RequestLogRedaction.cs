// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.Infrastructure.Logging;
using Honua.Infrastructure.Authentication;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace Honua.Infrastructure.Logging;

internal static class RequestLogConfiguration
{
    internal static LoggerConfiguration ConfigureHonuaRequestDiagnostics(this LoggerConfiguration configuration) =>
        configuration.MinimumLevel.Override("Microsoft.AspNetCore.Hosting", LogEventLevel.Information)
            // Enrichment runs before every sink, including UseSerilog's forwarded providers.
            .Enrich.With(new RequestLogRedaction());
}

internal sealed partial class RequestLogRedaction : ILogEventEnricher
{
    private const string Redacted = "[REDACTED]";

    internal static ITextFormatter CreateRedactingFormatter(ITextFormatter inner)
        => new RedactingTextFormatter(inner);

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        // Most properties come back unchanged; only those are left alone, so an event allocates for what
        // redaction actually rewrites. Updates wait until the enumeration over Properties has finished.
        List<LogEventProperty>? changed = null;
        foreach (var property in logEvent.Properties)
        {
            var redacted = Redact(property.Key, property.Value);
            if (!ReferenceEquals(redacted, property.Value))
            {
                (changed ??= []).Add(new LogEventProperty(property.Key, redacted));
            }
        }

        if (changed is null)
        {
            return;
        }

        foreach (var property in changed)
        {
            logEvent.AddOrUpdateProperty(property);
        }
    }

    internal static LogEvent RedactEvent(LogEvent logEvent)
    {
        if (logEvent.Exception is null)
        {
            return logEvent;
        }

        var scrubbedException = RedactException(logEvent.Exception);
        if (ReferenceEquals(scrubbedException, logEvent.Exception))
        {
            return logEvent;
        }

        return new LogEvent(
            logEvent.Timestamp,
            logEvent.Level,
            scrubbedException,
            logEvent.MessageTemplate,
            logEvent.Properties.Select(static kvp => new LogEventProperty(kvp.Key, kvp.Value)),
            logEvent.TraceId ?? default,
            logEvent.SpanId ?? default);
    }

    internal static Exception RedactException(Exception exception)
    {
        var rendered = exception.ToString();
        var scrubbedRendered = ScrubText(rendered);
        if (string.Equals(scrubbedRendered, rendered, StringComparison.Ordinal))
        {
            return exception;
        }

        var scrubbedMessage = ScrubText(exception.Message);
        var scrubbedStackTrace = exception.StackTrace is null ? null : ScrubText(exception.StackTrace);
        return new RedactedException(scrubbedMessage, scrubbedStackTrace, scrubbedRendered);
    }

    internal static string ScrubText(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        try
        {
            // Hosting start events run before routing, so matched endpoint metadata is
            // unavailable there. Scrub known capability paths, query credentials, auth
            // headers, JWTs, connection-string passwords, and URI userinfo at the same seam.
            var scrubbed = CredentialPathRedaction.Redact(text);
            scrubbed = QueryParameter().Replace(scrubbed, static match =>
                IsCredentialName(Uri.UnescapeDataString(match.Groups[2].Value))
                    ? string.Concat(match.Groups[1].Value, match.Groups[2].Value, "=", Redacted)
                    : match.Value);
            scrubbed = AuthorizationScheme().Replace(scrubbed, "$1 " + Redacted);
            scrubbed = RawJwt().Replace(scrubbed, Redacted);
            scrubbed = ConnectionStringSecret().Replace(scrubbed, "$1" + Redacted);
            return UriUserInfo().Replace(scrubbed, "$1" + Redacted + "@");
        }
        catch (RegexMatchTimeoutException)
        {
            // Fail closed on adversarial diagnostic values.
            return Redacted;
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

        if (IsSafeDiagnosticIdentifier(name, value))
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
            ScalarValue { Value: string text } => ScrubbedScalar(value, text),
            SequenceValue sequence => RedactSequence(sequence),
            StructureValue structure => RedactStructure(structure),
            DictionaryValue dictionary => RedactDictionary(dictionary),
            _ => value
        };
    }

    private static SequenceValue RedactSequence(SequenceValue sequence)
    {
        var changed = false;
        var elements = new LogEventPropertyValue[sequence.Elements.Count];
        for (var i = 0; i < sequence.Elements.Count; i++)
        {
            var original = sequence.Elements[i];
            var redacted = Redact(string.Empty, original);
            elements[i] = redacted;
            changed |= !ReferenceEquals(redacted, original);
        }

        return changed ? new SequenceValue(elements) : sequence;
    }

    private static StructureValue RedactStructure(StructureValue structure)
    {
        var changed = false;
        var properties = new LogEventProperty[structure.Properties.Count];
        for (var i = 0; i < structure.Properties.Count; i++)
        {
            var property = structure.Properties[i];
            var redacted = Redact(property.Name, property.Value);
            properties[i] = ReferenceEquals(redacted, property.Value)
                ? property
                : new LogEventProperty(property.Name, redacted);
            changed |= !ReferenceEquals(redacted, property.Value);
        }

        return changed ? new StructureValue(properties, structure.TypeTag) : structure;
    }

    private static DictionaryValue RedactDictionary(DictionaryValue dictionary)
    {
        var changed = false;
        var elements = new List<KeyValuePair<ScalarValue, LogEventPropertyValue>>(dictionary.Elements.Count);
        foreach (var element in dictionary.Elements)
        {
            var redacted = Redact(element.Key.Value as string ?? string.Empty, element.Value);
            elements.Add(new KeyValuePair<ScalarValue, LogEventPropertyValue>(element.Key, redacted));
            changed |= !ReferenceEquals(redacted, element.Value);
        }

        return changed ? new DictionaryValue(elements) : dictionary;
    }

    private static LogEventPropertyValue ScrubbedScalar(LogEventPropertyValue value, string text)
    {
        var scrubbed = ScrubText(text);
        return string.Equals(scrubbed, text, StringComparison.Ordinal) ? value : new ScalarValue(scrubbed);
    }

    private static bool IsSafeDiagnosticIdentifier(string name, LogEventPropertyValue value)
    {
        if (value is not ScalarValue { Value: string text })
        {
            return false;
        }

        // These existing diagnostics are derived by LogValueRedactor and the bounded
        // cache/rate-limit/session family classifiers, never raw cache keys or credentials.
        // Validate their shapes; the exemptions do not apply to URL parameter names.
        if (name.Equals("KeyHash", StringComparison.OrdinalIgnoreCase))
        {
            return text.Length == 8 && text.All(char.IsAsciiHexDigit);
        }

        return name.Equals("KeyFamily", StringComparison.OrdinalIgnoreCase) && text is
            "empty" or "layer" or "service" or "query" or "tile" or "catalog" or "replica" or
            "schema" or "auth" or "general" or "tenant" or "user" or "ip" or "unknown" or
            "admin-auth:pending" or "admin-auth:session" or "admin-auth:unknown";
    }

    private static bool IsCredentialName(string name) =>
        name.Equals(AdminHumanApprovalEndpointMetadata.CapabilityHeaderName, StringComparison.OrdinalIgnoreCase) ||
        name.Equals("capability", StringComparison.OrdinalIgnoreCase) ||
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

    [GeneratedRegex(@"([?&])([^?&=\s]+)=([^&\s]*)", RegexOptions.CultureInvariant, 100)]
    private static partial Regex QueryParameter();

    [GeneratedRegex(@"\b(Bearer|Basic|SharedKey(?:Lite)?)\s+([A-Za-z0-9\-._~+/=:]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex AuthorizationScheme();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{5,}(?:\.[A-Za-z0-9_-]*){0,2}\b", RegexOptions.CultureInvariant, 100)]
    private static partial Regex RawJwt();

    [GeneratedRegex(@"((?:^|[;,\s'""(])(?:Password|Pwd|Passphrase|AccountKey|SharedAccessKey|ClientSecret)\s*=\s*)(?:""[^""]*""|'[^']*'|[^;,\s'"")]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex ConnectionStringSecret();

    [GeneratedRegex(@"([a-zA-Z][a-zA-Z0-9+.-]*://)([^/\s:@]*):([^/\s@]*)@", RegexOptions.CultureInvariant, 100)]
    private static partial Regex UriUserInfo();

    private sealed class RedactingTextFormatter(ITextFormatter inner) : ITextFormatter
    {
        public void Format(LogEvent logEvent, TextWriter output)
            => inner.Format(RedactEvent(logEvent), output);
    }

    private sealed class RedactedException(string message, string? stackTrace, string rendered)
        : Exception(message)
    {
        public override string? StackTrace => stackTrace;

        public override string ToString() => rendered;
    }
}
