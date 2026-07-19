// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Ogc.Classic.Wps20;

internal sealed class Wps20ConformanceEcho : IDisposable
{
    internal const string Ets11ProcessAlias = "org.n52.javaps.test.EchoProcess";
    internal const int MaxPayloadCharacters = 65_536;
    internal const int MaxConcurrentReferenceFetches = 4;

    private readonly ConcurrentDictionary<string, StoredEchoResult> _results = new(StringComparer.Ordinal);
    private readonly object _storeLock = new();
    private readonly SemaphoreSlim _referenceFetchSlots = new(MaxConcurrentReferenceFetches, MaxConcurrentReferenceFetches);
    private readonly IOptionsMonitor<Wps20Options> _options;
    private readonly IHostEnvironment _environment;

    public Wps20ConformanceEcho(IOptionsMonitor<Wps20Options> options, IHostEnvironment environment)
    {
        _options = options;
        _environment = environment;
    }

    internal bool Enabled => _options.CurrentValue.EnableConformanceEcho && _environment.IsEnvironment("Test");

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

        if (!Uri.TryCreate(input.Value, UriKind.Absolute, out var referenceUri)
            || referenceUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(referenceUri.UserInfo)
            || referenceUri.IsLoopback
            || OutboundHttpUrlValidator.IsLocalhostHostName(referenceUri.Host))
        {
            throw new Wps20EchoException("Reference input must be a public HTTPS URL without credentials.");
        }

        var allowedHosts = _options.CurrentValue.ConformanceReferenceAllowedHosts;
        if (!allowedHosts.Contains(referenceUri.DnsSafeHost, StringComparer.OrdinalIgnoreCase))
        {
            throw new Wps20EchoException("Reference input host is not in the WPS conformance allowlist.");
        }

        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(referenceUri.DnsSafeHost, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ArgumentException)
        {
            throw new Wps20EchoException("Reference input host could not be resolved.");
        }
        if (addresses.Length == 0 || addresses.Any(OutboundHttpUrlValidator.IsPrivateOrReservedAddress))
        {
            throw new Wps20EchoException("Reference input resolves to a private or reserved network address.");
        }

        await _referenceFetchSlots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var pinnedAddress = addresses[0];
            using var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(pinnedAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(pinnedAddress, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
            using var request = new HttpRequestMessage(HttpMethod.Get, referenceUri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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
        finally
        {
            _referenceFetchSlots.Release();
        }
    }

    internal string BuildPublicUrl(HttpContext context, string relativePath)
    {
        var configured = _options.CurrentValue.PublicBaseUrl;
        if (!string.IsNullOrWhiteSpace(configured)
            && Uri.TryCreate(configured, UriKind.Absolute, out var publicBase)
            && (string.Equals(publicBase.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                || string.Equals(publicBase.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            && string.IsNullOrEmpty(publicBase.UserInfo))
        {
            return publicBase.ToString().TrimEnd('/') + relativePath;
        }
        return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}{relativePath}";
    }

    public void Dispose() => _referenceFetchSlots.Dispose();

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
