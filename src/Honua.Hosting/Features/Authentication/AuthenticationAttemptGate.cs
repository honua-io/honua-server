// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.RateLimiting;
using Microsoft.Extensions.Options;

namespace Honua.Infrastructure.Authentication;

// The trusted forwarded-headers middleware has already rewritten RemoteIpAddress, so forwarded headers are never read here.
// A request with no client address is neither counted nor refused: grouping those under one key would let one caller lock out all of them.
internal sealed class AuthenticationAttemptGate(
    IAuthenticationFailureLimiter limiter,
    IOptions<AuthenticationFailureLimiterOptions> options,
    ILogger<AuthenticationAttemptGate> logger)
{
    private readonly AuthenticationFailureLimiterOptions _options = options.Value;
    private readonly IPNetwork[] _exemptNetworks = [.. options.Value.ExemptNetworks.Select(network => IPNetwork.Parse(network))];
    private int _missingAddressReported;

    public AuthenticationAttempt Begin(IPAddress? client, string credential)
    {
        if (!_options.Enabled)
        {
            return AuthenticationAttempt.Untracked();
        }

        if (client is null)
        {
            ReportMissingAddressOnce();
            return AuthenticationAttempt.Untracked();
        }

        var address = client.IsIPv4MappedToIPv6 ? client.MapToIPv4() : client;
        if (_exemptNetworks.Any(network => network.Contains(address)))
        {
            return AuthenticationAttempt.Untracked();
        }

        var key = new AuthenticationAttemptKey(ClientAddressPartition.From(address)!, StringComparer.Ordinal.GetHashCode(credential));
        var admission = limiter.TryBeginAttempt(key);
        return admission.Admitted
            ? AuthenticationAttempt.Tracked(limiter, key)
            : AuthenticationAttempt.Refused(admission.RetryAfter);
    }

    private void ReportMissingAddressOnce()
    {
        if (Interlocked.Exchange(ref _missingAddressReported, 1) == 0)
        {
            AuthenticationLog.AuthenticationSourceUnavailable(logger);
        }
    }
}

// Disposing ends the attempt; it counts as a failure only after CountFailure.
internal sealed class AuthenticationAttempt : IDisposable
{
    private readonly IAuthenticationFailureLimiter? _limiter;
    private readonly AuthenticationAttemptKey _key;
    private AuthenticationAttemptOutcome _outcome;
    private bool _ended;

    private AuthenticationAttempt(IAuthenticationFailureLimiter? limiter, AuthenticationAttemptKey key, TimeSpan? retryAfter)
    {
        _limiter = limiter;
        _key = key;
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }

    internal static AuthenticationAttempt Untracked() => new(null, default, null);

    internal static AuthenticationAttempt Refused(TimeSpan retryAfter) => new(null, default, retryAfter);

    internal static AuthenticationAttempt Tracked(IAuthenticationFailureLimiter limiter, AuthenticationAttemptKey key) => new(limiter, key, null);

    public void CountFailure() => _outcome = AuthenticationAttemptOutcome.Failed;

    public void Dispose()
    {
        if (_limiter is null || _ended)
        {
            return;
        }

        _ended = true;
        _limiter.EndAttempt(_key, _outcome);
    }
}

internal static class TooManyAttemptsResponse
{
    private const string Detail = "Too many failed authentication attempts. Try again later.";

    internal static IResult Create(HttpContext context, TimeSpan retryAfter)
        => StandardErrorHelpers.CreateTooManyRequests(context, Detail, Math.Max(1, (int)Math.Ceiling(retryAfter.TotalSeconds)));
}
