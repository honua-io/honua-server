// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Infrastructure.Hosting;

internal interface IServerInstanceIdentity : Honua.Core.Features.Infrastructure.Abstractions.IInstanceIdentity
{
    string Source { get; }

    DateTimeOffset StartedAt { get; }
}

internal static class ServerInstanceIdentitySources
{
    public const string Configuration = "configuration";

    public const string AwsLambda = "aws-lambda";

    public const string AwsEcsTask = "aws-ecs-task";

    public const string AwsEcsContainer = "aws-ecs-container";

    public const string Hostname = "hostname";

    public const string MachineName = "machine-name";

    public const string Generated = "generated";
}

// Identity is resolved without network calls, and nothing secret is read.
internal sealed class ServerInstanceIdentity : IServerInstanceIdentity
{
    public const string ConfigurationKey = "Server:InstanceId";

    public const string HeaderName = "Honua-Instance";

    internal const int MaxLength = 128;

    private ServerInstanceIdentity(string instanceId, string source, DateTimeOffset startedAt)
    {
        InstanceId = instanceId;
        Source = source;
        StartedAt = startedAt;
    }

    public string InstanceId { get; }

    public string Source { get; }

    public DateTimeOffset StartedAt { get; }

    public static ServerInstanceIdentity Resolve(IConfiguration configuration, TimeProvider clock)
        => Resolve(configuration, Environment.GetEnvironmentVariable, TryGetMachineName(), clock);

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

    // An unrecognized ECS metadata segment is hashed because the raw segment addresses the task-local metadata endpoint.
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

internal static class ServerInstanceIdentityServiceCollectionExtensions
{
    private static readonly IConfiguration EmptyConfiguration = new ConfigurationBuilder().Build();

    public static IServiceCollection AddServerInstanceIdentity(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        // Hosts that compose only the operations slice (no IConfiguration registered) still get a
        // usable identity from the environment sources.
        services.TryAddSingleton<IServerInstanceIdentity>(static sp => ServerInstanceIdentity.Resolve(
            sp.GetService<IConfiguration>() ?? EmptyConfiguration,
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.TryAddSingleton<Honua.Core.Features.Infrastructure.Abstractions.IInstanceIdentity>(
            static sp => sp.GetRequiredService<IServerInstanceIdentity>());
        return services;
    }
}
