// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Hosting;

/// <summary>
/// Stable identity of the running server process/host, resolved once at startup. Lets an
/// external observer (the release harness, an operator) tell which replica behind a load
/// balancer or Lambda alias answered a request, so a cross-replica read can be proven.
/// </summary>
internal interface IServerInstanceIdentity
{
    /// <summary>Identifier of this instance; stable for the life of the process.</summary>
    string InstanceId { get; }

    /// <summary>Which source produced <see cref="InstanceId"/> (see <see cref="ServerInstanceIdentitySources"/>).</summary>
    string Source { get; }

    /// <summary>When this instance resolved its identity (process start, UTC).</summary>
    DateTimeOffset StartedAt { get; }
}

/// <summary>Names of the sources an instance identity can be resolved from, in precedence order.</summary>
internal static class ServerInstanceIdentitySources
{
    /// <summary>Operator-set <c>Server:InstanceId</c>.</summary>
    public const string Configuration = "configuration";

    /// <summary><c>AWS_LAMBDA_LOG_STREAM_NAME</c>: names the Lambda execution environment.</summary>
    public const string AwsLambda = "aws-lambda";

    /// <summary>ECS task id parsed from <c>ECS_CONTAINER_METADATA_URI_V4</c> (the task ARN suffix).</summary>
    public const string AwsEcsTask = "aws-ecs-task";

    /// <summary>Hash of the <c>ECS_CONTAINER_METADATA_URI_V4</c> container segment when no task id is parseable.</summary>
    public const string AwsEcsContainer = "aws-ecs-container";

    /// <summary><c>HOSTNAME</c>: the Kubernetes pod name or the container hostname.</summary>
    public const string Hostname = "hostname";

    /// <summary><see cref="Environment.MachineName"/>.</summary>
    public const string MachineName = "machine-name";

    /// <summary>A random identifier generated for this process.</summary>
    public const string Generated = "generated";
}

/// <summary>
/// Resolves <see cref="IServerInstanceIdentity"/> from configuration and environment variables
/// only: no network calls (for example to the ECS task metadata endpoint) happen at startup.
/// </summary>
/// <remarks>
/// Precedence: <c>Server:InstanceId</c> configuration, <c>AWS_LAMBDA_LOG_STREAM_NAME</c>,
/// the ECS task id in <c>ECS_CONTAINER_METADATA_URI_V4</c>, <c>HOSTNAME</c>,
/// <see cref="Environment.MachineName"/>, then a random per-process id. Values are restricted to
/// a header/log-safe character set and length; nothing secret is read.
/// </remarks>
internal sealed class ServerInstanceIdentity : IServerInstanceIdentity
{
    /// <summary>Configuration key an operator sets to pin the instance id.</summary>
    public const string ConfigurationKey = "Server:InstanceId";

    /// <summary>Response header carrying <see cref="InstanceId"/>.</summary>
    public const string HeaderName = "Honua-Instance";

    internal const int MaxLength = 128;

    private ServerInstanceIdentity(string instanceId, string source, DateTimeOffset startedAt)
    {
        InstanceId = instanceId;
        Source = source;
        StartedAt = startedAt;
    }

    /// <inheritdoc />
    public string InstanceId { get; }

    /// <inheritdoc />
    public string Source { get; }

    /// <inheritdoc />
    public DateTimeOffset StartedAt { get; }

    /// <summary>Resolves the identity from the live process environment.</summary>
    public static ServerInstanceIdentity Resolve(IConfiguration configuration, TimeProvider clock)
        => Resolve(configuration, Environment.GetEnvironmentVariable, TryGetMachineName(), clock);

    /// <summary>Resolves the identity from the supplied configuration and environment.</summary>
    public static ServerInstanceIdentity Resolve(
        IConfiguration configuration,
        Func<string, string?> environment,
        string? machineName,
        TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(clock);

        var startedAt = clock.GetUtcNow();

        if (Sanitize(configuration[ConfigurationKey]) is { } configured)
        {
            return new(configured, ServerInstanceIdentitySources.Configuration, startedAt);
        }

        if (Sanitize(environment("AWS_LAMBDA_LOG_STREAM_NAME")) is { } lambda)
        {
            return new(lambda, ServerInstanceIdentitySources.AwsLambda, startedAt);
        }

        if (TryResolveEcs(environment("ECS_CONTAINER_METADATA_URI_V4"), out var ecsId, out var ecsSource))
        {
            return new(ecsId, ecsSource, startedAt);
        }

        if (Sanitize(environment("HOSTNAME")) is { } hostname)
        {
            return new(hostname, ServerInstanceIdentitySources.Hostname, startedAt);
        }

        if (Sanitize(machineName) is { } machine)
        {
            return new(machine, ServerInstanceIdentitySources.MachineName, startedAt);
        }

        return new(Guid.NewGuid().ToString("N"), ServerInstanceIdentitySources.Generated, startedAt);
    }

    /// <summary>
    /// The ECS v4 metadata URI ends in a per-container segment; on Fargate it is
    /// <c>&lt;32-hex task id&gt;-&lt;number&gt;</c>, and the task id is the task ARN suffix. Any
    /// other shape is hashed rather than echoed, since the raw segment addresses the task-local
    /// metadata endpoint.
    /// </summary>
    private static bool TryResolveEcs(string? metadataUri, out string instanceId, out string source)
    {
        instanceId = string.Empty;
        source = string.Empty;
        if (string.IsNullOrWhiteSpace(metadataUri))
        {
            return false;
        }

        var trimmed = metadataUri.Trim().TrimEnd('/');
        var lastSlash = trimmed.LastIndexOf('/');
        var segment = lastSlash >= 0 ? trimmed[(lastSlash + 1)..] : trimmed;
        if (segment.Length == 0)
        {
            return false;
        }

        var dash = segment.IndexOf('-', StringComparison.Ordinal);
        if (dash == 32 && IsLowerHex(segment.AsSpan(0, 32)))
        {
            instanceId = segment[..32];
            source = ServerInstanceIdentitySources.AwsEcsTask;
            return true;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(segment));
        instanceId = "ecs-" + Convert.ToHexStringLower(hash.AsSpan(0, 8));
        source = ServerInstanceIdentitySources.AwsEcsContainer;
        return true;
    }

    private static bool IsLowerHex(ReadOnlySpan<char> value)
    {
        foreach (var c in value)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'f')))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Restricts a candidate value to ASCII letters, digits and <c>-_.:/[]$</c> (every other
    /// character becomes <c>-</c>) and caps it at <see cref="MaxLength"/>, so it is safe as a
    /// header value and a structured log property. Blank input resolves to null.
    /// </summary>
    internal static string? Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > MaxLength)
        {
            trimmed = trimmed[..MaxLength];
        }

        return string.Create(trimmed.Length, trimmed, static (span, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var c = source[i];
                var allowed = c is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9')
                    or '-' or '_' or '.' or ':' or '/' or '[' or ']' or '$';
                span[i] = allowed ? c : '-';
            }
        });
    }

    private static string? TryGetMachineName()
    {
        try
        {
            return Environment.MachineName;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>Registers the per-instance identity.</summary>
internal static class ServerInstanceIdentityServiceCollectionExtensions
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    /// <summary>
    /// Registers <see cref="IServerInstanceIdentity"/> as a singleton resolved from configuration
    /// and the process environment. Idempotent.
    /// </summary>
    public static IServiceCollection AddServerInstanceIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Hosts that compose only the operations slice (no IConfiguration registered) still get a
        // usable identity from the environment sources.
        services.TryAddSingleton<IServerInstanceIdentity>(static sp => ServerInstanceIdentity.Resolve(
            sp.GetService<IConfiguration>() ?? EmptyConfiguration,
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        return services;
    }
}
