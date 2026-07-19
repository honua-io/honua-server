// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Ogc.Classic.Wps20;

internal sealed class Wps20ConformanceEcho
{
    internal const string ReferenceClientName = "Wps20ConformanceReference";
    internal const string Ets11ProcessAlias = "org.n52.javaps.test.EchoProcess";
    internal const int MaxPayloadCharacters = 65_536;

    private readonly ConcurrentDictionary<string, StoredEchoResult> _results = new(StringComparer.Ordinal);
    private readonly object _storeLock = new();
    private readonly IOptionsMonitor<Wps20Options> _options;
    private readonly IHttpClientFactory _httpClientFactory;

    public Wps20ConformanceEcho(IOptionsMonitor<Wps20Options> options, IHttpClientFactory httpClientFactory)
    {
        _options = options;
        _httpClientFactory = httpClientFactory;
    }

    internal bool Enabled => _options.CurrentValue.EnableConformanceEcho;

    internal string ProcessId => _options.CurrentValue.ConformanceEchoProcessId;

    internal bool IsEchoProcess(string? processId) =>
        Enabled && (string.Equals(processId, ProcessId, StringComparison.Ordinal)
            || string.Equals(processId, Ets11ProcessAlias, StringComparison.Ordinal));

    internal async Task<EchoValue> ResolveInputAsync(EchoInput input, CancellationToken cancellationToken)
    {
        if (input.Kind != EchoValueKind.Reference)
        {
            return new EchoValue(input.Kind, input.Value, input.MimeType);
        }

        var validation = await OutboundHttpUrlValidator.ValidateAsync(input.Value, cancellationToken).ConfigureAwait(false);
        if (!validation.IsValid || validation.Uri is null)
        {
            throw new Wps20EchoException($"Reference input {validation.ErrorMessage}");
        }

        var allowedHosts = _options.CurrentValue.ConformanceReferenceAllowedHosts;
        if (!allowedHosts.Contains(validation.Uri.DnsSafeHost, StringComparer.OrdinalIgnoreCase))
        {
            throw new Wps20EchoException("Reference input host is not in the WPS conformance allowlist.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, validation.Uri);
        using var response = await _httpClientFactory.CreateClient(ReferenceClientName)
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is >= HttpStatusCode.MultipleChoices and < HttpStatusCode.BadRequest)
        {
            throw new Wps20EchoException("Reference input redirects are not allowed.");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new Wps20EchoException("Reference input could not be retrieved.");
        }
        if (response.Content.Headers.ContentLength is > MaxPayloadCharacters)
        {
            throw new Wps20EchoException("Reference input exceeds the bounded payload limit.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream(MaxPayloadCharacters + 1);
        var block = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            if (buffer.Length + read > MaxPayloadCharacters)
            {
                throw new Wps20EchoException("Reference input exceeds the bounded payload limit.");
            }
            buffer.Write(block, 0, read);
        }

        var mimeType = response.Content.Headers.ContentType?.MediaType ?? input.MimeType ?? "text/plain";
        return new EchoValue(EchoValueKind.Literal, Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), mimeType);
    }

    internal string Store(EchoValue value, string outputId, string transmission, string responseForm)
    {
        var now = DateTimeOffset.UtcNow;
        var options = _options.CurrentValue;
        var ttl = TimeSpan.FromSeconds(Math.Clamp(options.ConformanceJobTtlSeconds, 1, 3600));
        var capacity = Math.Clamp(options.ConformanceJobCapacity, 1, 1024);

        lock (_storeLock)
        {
            foreach (var expired in _results.Where(pair => pair.Value.ExpiresAt <= now).Select(pair => pair.Key))
            {
                _results.TryRemove(expired, out _);
            }
            while (_results.Count >= capacity)
            {
                var oldest = _results.MinBy(pair => pair.Value.CreatedAt);
                if (oldest.Key is null || !_results.TryRemove(oldest.Key, out _))
                {
                    break;
                }
            }

            var token = "wps-echo-" + Guid.NewGuid().ToString("N");
            _results[token] = new StoredEchoResult(value, outputId, transmission, responseForm, now, now.Add(ttl));
            return token;
        }
    }

    internal bool TryGet(string token, out StoredEchoResult result)
    {
        if (_results.TryGetValue(token, out result!) && result.ExpiresAt > DateTimeOffset.UtcNow)
        {
            return true;
        }
        _results.TryRemove(token, out _);
        result = null!;
        return false;
    }

    internal sealed record StoredEchoResult(
        EchoValue Value,
        string OutputId,
        string Transmission,
        string ResponseForm,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);
}

internal enum EchoValueKind
{
    Literal,
    Complex,
    BoundingBox,
    Reference
}

internal sealed record EchoInput(string Id, EchoValueKind Kind, string Value, string? MimeType);

internal sealed record EchoValue(EchoValueKind Kind, string Value, string? MimeType);

internal sealed class Wps20EchoException(string message) : Exception(message);
