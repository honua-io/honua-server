// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.Lambda;
using Amazon.Lambda.Model;

namespace Honua.ControlPlane;

/// <summary>
/// Publishes an immutable container image as a numbered Lambda version.
/// Keeps image publication separate from revision-guarded alias movement so a failed
/// publish cannot move the alias.
/// </summary>
internal sealed record LambdaImageAlias(string Version, string RevisionId, bool HasWeightedTraffic);

internal interface IAwsLambdaImageClient
{
    Task<LambdaImageAlias> GetAliasAsync(string functionName, string aliasName, string? region, string? serviceUrl, CancellationToken cancellationToken);

    Task MoveAliasAsync(string functionName, string aliasName, string version, string expectedRevisionId, string? region, string? serviceUrl, CancellationToken cancellationToken);

    /// <summary>
    /// Updates the function image, waits until the update succeeds, and publishes a version.
    /// Returns the numeric version. Does not change an alias.
    /// </summary>
    Task<string> PublishImageAsync(
        string functionName,
        string imageUri,
        string? region,
        string? serviceUrl,
        int maxAttempts,
        int delayMilliseconds,
        CancellationToken cancellationToken = default);
}

internal sealed class AwsSdkLambdaImageClient : IAwsLambdaImageClient
{
    public async Task<LambdaImageAlias> GetAliasAsync(string functionName, string aliasName, string? region, string? serviceUrl, CancellationToken cancellationToken)
    {
        using var client = AwsSdkLambdaAliasClient.CreateClient(region, serviceUrl);
        var alias = await client.GetAliasAsync(new GetAliasRequest { FunctionName = functionName, Name = aliasName }, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(alias.RevisionId) || !long.TryParse(alias.FunctionVersion, out var version) || version <= 0)
        {
            throw new InvalidOperationException("Lambda alias must point to a numbered version and have a revision ID.");
        }

        return new LambdaImageAlias(alias.FunctionVersion, alias.RevisionId, alias.RoutingConfig?.AdditionalVersionWeights?.Count > 0);
    }

    public async Task MoveAliasAsync(string functionName, string aliasName, string version, string expectedRevisionId, string? region, string? serviceUrl, CancellationToken cancellationToken)
    {
        using var client = AwsSdkLambdaAliasClient.CreateClient(region, serviceUrl);
        await client.UpdateAliasAsync(new UpdateAliasRequest
        {
            FunctionName = functionName,
            Name = aliasName,
            FunctionVersion = version,
            RevisionId = expectedRevisionId
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> PublishImageAsync(
        string functionName,
        string imageUri,
        string? region,
        string? serviceUrl,
        int maxAttempts,
        int delayMilliseconds,
        CancellationToken cancellationToken = default)
    {
        using var client = AwsSdkLambdaAliasClient.CreateClient(region, serviceUrl);
        var current = await client.GetFunctionConfigurationAsync(
                new GetFunctionConfigurationRequest { FunctionName = functionName },
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(current.PackageType?.Value, PackageType.Image.Value, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Lambda image convergence requires a function whose package type is Image.");
        }

        var updated = await client.UpdateFunctionCodeAsync(
                new UpdateFunctionCodeRequest
                {
                    FunctionName = functionName,
                    ImageUri = imageUri
                },
                cancellationToken)
            .ConfigureAwait(false);
        if (!await WaitUntilSuccessfulAsync(client, functionName, updated.LastUpdateStatus, maxAttempts, delayMilliseconds, cancellationToken)
                .ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                "Lambda function image update did not succeed. The alias was not moved.");
        }

        var published = await client.PublishVersionAsync(
                BuildPublishRequest(functionName, updated.CodeSha256, updated.RevisionId),
                cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(published.Version) ||
            !long.TryParse(published.Version, out var version) ||
            version <= 0)
        {
            throw new InvalidOperationException(
                "Lambda image publish did not return a numeric version. The alias was not moved.");
        }

        var publishedFunction = await client.GetFunctionAsync(
                new GetFunctionRequest { FunctionName = functionName, Qualifier = published.Version },
                cancellationToken)
            .ConfigureAwait(false);
        if (!string.Equals(publishedFunction.Code?.ResolvedImageUri, imageUri, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Published Lambda image does not match the approved image. The alias was not moved.");
        }

        return published.Version;
    }

    internal static PublishVersionRequest BuildPublishRequest(string functionName, string? codeSha256, string? revisionId)
    {
        if (string.IsNullOrWhiteSpace(codeSha256) || string.IsNullOrWhiteSpace(revisionId))
        {
            throw new InvalidOperationException("Lambda update did not return publication preconditions. The alias was not moved.");
        }

        return new PublishVersionRequest { FunctionName = functionName, CodeSha256 = codeSha256, RevisionId = revisionId };
    }

    private static async Task<bool> WaitUntilSuccessfulAsync(
        AmazonLambdaClient client,
        string functionName,
        LastUpdateStatus? initial,
        int maxAttempts,
        int delayMilliseconds,
        CancellationToken cancellationToken)
    {
        var attempts = Math.Clamp(maxAttempts, 1, 60);
        var delay = Math.Clamp(delayMilliseconds, 0, 30_000);
        var status = initial;
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (IsStatus(status, LastUpdateStatus.Successful))
            {
                return true;
            }

            if (IsStatus(status, LastUpdateStatus.Failed))
            {
                return false;
            }

            if (attempt > 0 && delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            else if (attempt == 0 && IsStatus(status, LastUpdateStatus.InProgress) && delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }

            var configuration = await client.GetFunctionConfigurationAsync(
                    new GetFunctionConfigurationRequest { FunctionName = functionName },
                    cancellationToken)
                .ConfigureAwait(false);
            status = configuration.LastUpdateStatus;
        }

        return IsStatus(status, LastUpdateStatus.Successful);
    }

    private static bool IsStatus(LastUpdateStatus? status, LastUpdateStatus expected)
        => string.Equals(status?.Value, expected.Value, StringComparison.Ordinal);
}
