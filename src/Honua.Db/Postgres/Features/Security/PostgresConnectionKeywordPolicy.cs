// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Npgsql;

namespace Honua.Db.Postgres.Features.Security;

/// <summary>
/// Keywords a resolved PostgreSQL secure-connection string may carry (SEC-23): the endpoint,
/// credentials, TLS mode and connection/pool tuning. Keywords that load server-side files, change
/// certificate validation or pass startup options to the database are refused, so a stored or
/// secret-referenced string cannot widen what the connection does beyond those settings.
/// </summary>
internal static class PostgresConnectionKeywordPolicy
{
    // Compared after removing spaces and lower-casing, so "SSL Mode", "SslMode" and "sslmode" match.
    private static readonly HashSet<string> _allowedKeywords = new(StringComparer.Ordinal)
    {
        // Endpoint and credentials (including the documented synonyms).
        "host", "server", "port", "database", "db",
        "username", "userid", "user", "uid", "password", "pwd", "psw",
        // Transport security that only tightens the session.
        "sslmode", "sslnegotiation", "channelbinding", "requireauth",
        // Timeouts, pooling and keepalive.
        "timeout", "connecttimeout", "commandtimeout", "cancellationtimeout",
        "pooling", "minimumpoolsize", "minpoolsize", "maximumpoolsize", "maxpoolsize",
        "connectionidlelifetime", "connectionpruninginterval", "connectionlifetime",
        "keepalive", "tcpkeepalive", "tcpkeepalivetime", "tcpkeepaliveinterval",
        // Multi-host routing; every listed host is still checked against the host policy.
        "targetsessionattributes", "loadbalancehosts", "hostrecheckseconds",
        // Session and protocol tuning.
        "applicationname", "searchpath", "clientencoding", "encoding", "timezone",
        "maxautoprepare", "autoprepareminusages", "noresetonclose", "enlist",
        "readbuffersize", "writebuffersize", "socketreceivebuffersize", "socketsendbuffersize",
    };

    /// <summary>
    /// Returns the first keyword in <paramref name="builder"/> outside the allowlist, or
    /// <see langword="null"/> when every keyword is allowed.
    /// </summary>
    public static string? FindDisallowedKeyword(NpgsqlConnectionStringBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        foreach (var keyword in builder.Keys)
        {
            var normalized = Normalize(keyword);
            if (_allowedKeywords.Contains(normalized))
            {
                continue;
            }

            // Spelling out the default ("Trust Server Certificate=false") cannot relax validation.
            if (normalized == "trustservercertificate" && builder[keyword] is false)
            {
                continue;
            }

            return keyword;
        }

        return null;
    }

    /// <summary>
    /// Splits the builder's host list (<c>host1,host2:5433</c>) into bare host names.
    /// </summary>
    public static IReadOnlyList<string> SplitHosts(string? hostList)
    {
        if (string.IsNullOrWhiteSpace(hostList))
        {
            return [];
        }

        var hosts = new List<string>();
        foreach (var entry in hostList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            hosts.Add(StripPort(entry));
        }

        return hosts;
    }

    private static string StripPort(string entry)
    {
        // Bracketed IPv6 literal, optionally followed by :port.
        if (entry.StartsWith('['))
        {
            var close = entry.IndexOf(']', StringComparison.Ordinal);
            return close > 0 ? entry[1..close] : entry;
        }

        // A single colon separates host and port; more than one means a bare IPv6 literal.
        var colon = entry.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 && colon == entry.LastIndexOf(':')
            ? entry[..colon]
            : entry;
    }

    private static string Normalize(string keyword)
        => keyword.Replace(" ", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
}
