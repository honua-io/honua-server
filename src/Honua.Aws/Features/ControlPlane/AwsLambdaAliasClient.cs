// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

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
        string? serviceUrl = null,
        CancellationToken cancellationToken = default);

    Task<AwsLambdaAliasState> UpdateAliasAsync(
        string functionName,
        string aliasName,
        string functionVersion,
        IReadOnlyDictionary<string, double>? additionalVersionWeights,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default);
}

internal sealed class AwsSdkLambdaAliasClient : IAwsLambdaAliasClient
{
    public async Task<AwsLambdaAliasState> GetAliasAsync(
        string functionName,
        string aliasName,
        string? region,
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region, serviceUrl);
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
        string? serviceUrl = null,
        CancellationToken cancellationToken = default)
    {
        using var client = CreateClient(region, serviceUrl);
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

    // Visible for testing. Applies an opt-in, config-driven ServiceURL override (for example a
    // LocalStack Community Lambda endpoint) when supplied; when unset the client uses the default
    // regional endpoint, keeping production behaviour unchanged.
    internal static AmazonLambdaClient CreateClient(string? region, string? serviceUrl)
    {
        var config = new AmazonLambdaConfig();

        if (!string.IsNullOrWhiteSpace(region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
        }

        // Mirrors AwsS3FileStorage.CreateClient: when an explicit endpoint is supplied it takes
        // precedence for the actual request URL while the region (when set) still provides the
        // SigV4 signing region. Unset = default regional endpoint, keeping production behaviour.
        if (!string.IsNullOrWhiteSpace(serviceUrl))
        {
            config.ServiceURL = serviceUrl;
        }

        return new AmazonLambdaClient(config);
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
