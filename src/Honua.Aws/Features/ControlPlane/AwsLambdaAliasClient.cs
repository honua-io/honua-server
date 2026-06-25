// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Concurrent;
using Amazon;
using Amazon.Lambda;
using Amazon.Lambda.Model;

namespace Honua.ControlPlane;

internal sealed record AwsLambdaAliasState
{
    public required string AliasName { get; init; }

    public string? AliasArn { get; init; }

    public string? AliasInvokeArn { get; init; }

    public string? FunctionVersion { get; init; }

    public IReadOnlyDictionary<string, double> AdditionalVersionWeights { get; init; } = new Dictionary<string, double>(StringComparer.Ordinal);
}

internal interface IAwsLambdaAliasClient
{
    Task<AwsLambdaAliasState> GetAliasAsync(
        string functionName,
        string aliasName,
        string? region,
        CancellationToken cancellationToken = default);

    Task<AwsLambdaAliasState> UpdateAliasAsync(
        string functionName,
        string aliasName,
        string functionVersion,
        IReadOnlyDictionary<string, double>? additionalVersionWeights,
        string? region,
        CancellationToken cancellationToken = default);
}

internal sealed class AwsSdkLambdaAliasClient : IAwsLambdaAliasClient, IDisposable
{
    // AWS SDK clients are thread-safe and meant to be reused for the process lifetime. This client
    // is a singleton, but its construction varies by region, so cache one AmazonLambdaClient per
    // resolved region rather than building (and discarding) one per call.
    private readonly ConcurrentDictionary<string, AmazonLambdaClient> _clients =
        new(StringComparer.Ordinal);

    public async Task<AwsLambdaAliasState> GetAliasAsync(
        string functionName,
        string aliasName,
        string? region,
        CancellationToken cancellationToken = default)
    {
        var client = GetClient(region);
        var response = await client.GetAliasAsync(
            new GetAliasRequest
            {
                FunctionName = functionName,
                Name = aliasName
            },
            cancellationToken).ConfigureAwait(false);

        return ToState(
            response.Name,
            response.AliasArn,
            response.FunctionVersion,
            response.RoutingConfig?.AdditionalVersionWeights);
    }

    public async Task<AwsLambdaAliasState> UpdateAliasAsync(
        string functionName,
        string aliasName,
        string functionVersion,
        IReadOnlyDictionary<string, double>? additionalVersionWeights,
        string? region,
        CancellationToken cancellationToken = default)
    {
        var client = GetClient(region);
        var response = await client.UpdateAliasAsync(
            new UpdateAliasRequest
            {
                FunctionName = functionName,
                Name = aliasName,
                FunctionVersion = functionVersion,
                RoutingConfig = new AliasRoutingConfiguration
                {
                    AdditionalVersionWeights = additionalVersionWeights is { Count: > 0 }
                        ? new Dictionary<string, double>(additionalVersionWeights, StringComparer.Ordinal)
                        : new Dictionary<string, double>(StringComparer.Ordinal)
                }
            },
            cancellationToken).ConfigureAwait(false);

        return ToState(
            response.Name,
            response.AliasArn,
            response.FunctionVersion,
            response.RoutingConfig?.AdditionalVersionWeights);
    }

    private AmazonLambdaClient GetClient(string? region)
    {
        var key = string.IsNullOrWhiteSpace(region) ? string.Empty : region;
        return _clients.GetOrAdd(key, static k => string.IsNullOrEmpty(k)
            ? new AmazonLambdaClient()
            : new AmazonLambdaClient(RegionEndpoint.GetBySystemName(k)));
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    private static AwsLambdaAliasState ToState(
        string aliasName,
        string? aliasArn,
        string? functionVersion,
        IDictionary<string, double>? additionalVersionWeights)
        => new()
        {
            AliasName = aliasName,
            AliasArn = aliasArn,
            AliasInvokeArn = aliasArn,
            FunctionVersion = functionVersion,
            AdditionalVersionWeights = additionalVersionWeights is { Count: > 0 } weights
                ? new Dictionary<string, double>(weights, StringComparer.Ordinal)
                : new Dictionary<string, double>(StringComparer.Ordinal)
        };
}
