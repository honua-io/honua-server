// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Internal;
using Honua.Core.Features.Infrastructure.Validation;
using Microsoft.Extensions.Options;

namespace Honua.Protocols.Ogc.Classic.Wps20;

internal sealed class Wps20ConformanceEcho : IDisposable
{
    internal const string Ets11ProcessAlias = "org.n52.javaps.test.EchoProcess";
    internal const int MaxPayloadCharacters = 65_536;
    internal const int MaxConcurrentReferenceFetches = 4;
    private static readonly TimeSpan ReferenceFetchTimeout = TimeSpan.FromSeconds(10);

    private readonly ConcurrentDictionary<string, StoredEchoResult> _results = new(StringComparer.Ordinal);
    private readonly object _storeLock = new();
    private readonly SemaphoreSlim _referenceFetchSlots = new(MaxConcurrentReferenceFetches, MaxConcurrentReferenceFetches);
    private readonly IOptionsMonitor<Wps20Options> _options;
    private readonly IHostEnvironment _environment;
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> _resolveAddresses;
    private readonly Func<IReadOnlyList<IPAddress>, HttpMessageHandler> _createHandler;
    private readonly TimeSpan _referenceFetchTimeout;

    public Wps20ConformanceEcho(IOptionsMonitor<Wps20Options> options, IHostEnvironment environment)
        : this(
            options,
            environment,
            static (host, token) => Dns.GetHostAddressesAsync(host, token),
            CreateReferenceHandler,
            ReferenceFetchTimeout)
    {
    }

    internal Wps20ConformanceEcho(
        IOptionsMonitor<Wps20Options> options,
        IHostEnvironment environment,
        Func<string, CancellationToken, Task<IPAddress[]>> resolveAddresses,
        Func<IReadOnlyList<IPAddress>, HttpMessageHandler> createHandler,
        TimeSpan referenceFetchTimeout)
    {
        _options = options;
        _environment = environment;
        _resolveAddresses = resolveAddresses;
        _createHandler = createHandler;
        _referenceFetchTimeout = referenceFetchTimeout;
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

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(_referenceFetchTimeout);
        var boundedToken = deadline.Token;
        var slotAcquired = false;
        try
        {
            await _referenceFetchSlots.WaitAsync(boundedToken).ConfigureAwait(false);
            slotAcquired = true;

            IPAddress[] addresses;
            try
            {
                addresses = await _resolveAddresses(referenceUri.DnsSafeHost, boundedToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is SocketException or ArgumentException)
            {
                throw new Wps20EchoException("Reference input host could not be resolved.");
            }
            if (addresses.Length == 0 || addresses.Any(OutboundHttpUrlValidator.IsPrivateOrReservedAddress))
            {
                throw new Wps20EchoException("Reference input resolves to a private or reserved network address.");
            }

            var pinnedAddresses = addresses
                .OrderBy(address => address.AddressFamily)
                .ThenBy(address => Convert.ToHexString(address.GetAddressBytes()), StringComparer.Ordinal)
                .ToArray();

            using var handler = _createHandler(pinnedAddresses);
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var request = new HttpRequestMessage(HttpMethod.Get, referenceUri);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                boundedToken).ConfigureAwait(false);
            using (response)
            {
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

                await using var stream = await response.Content.ReadAsStreamAsync(boundedToken).ConfigureAwait(false);
                using var buffer = new MemoryStream(MaxPayloadCharacters + 1);
                var block = new byte[8192];
                while (true)
                {
                    var read = await stream.ReadAsync(block, boundedToken).ConfigureAwait(false);
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
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            throw new Wps20EchoException("Reference input retrieval timed out.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or SocketException)
        {
            throw new Wps20EchoException("Reference input could not be retrieved securely.");
        }
        finally
        {
            if (slotAcquired)
            {
                _referenceFetchSlots.Release();
            }
        }
    }

    private static HttpMessageHandler CreateReferenceHandler(IReadOnlyList<IPAddress> pinnedAddresses) =>
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ConnectCallback = (context, token) => ConnectPinnedAsync(
                pinnedAddresses,
                context.DnsEndPoint.Port,
                token)
        };

    internal static async ValueTask<Stream> ConnectPinnedAsync(
        IReadOnlyList<IPAddress> pinnedAddresses,
        int port,
        CancellationToken cancellationToken,
        Func<AddressFamily, Socket>? createSocket = null)
    {
        createSocket ??= static family => new Socket(family, SocketType.Stream, ProtocolType.Tcp);
        Exception? lastFailure = null;
        foreach (var pinnedAddress in pinnedAddresses)
        {
            using var socketOwner = new SocketConnectionOwner(createSocket(pinnedAddress.AddressFamily));
            try
            {
                await socketOwner.Socket
                    .ConnectAsync(new IPEndPoint(pinnedAddress, port), cancellationToken)
                    .ConfigureAwait(false);
                return socketOwner.TransferToNetworkStream();
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastFailure = ex;
            }
        }
        throw new HttpRequestException("No validated reference address accepted the connection.", lastFailure);
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
        return $"{context.Request.PathBase}{relativePath}";
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
