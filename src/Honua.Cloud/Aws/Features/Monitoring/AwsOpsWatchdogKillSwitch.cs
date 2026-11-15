// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.Runtime;
using Amazon.SimpleSystemsManagement.Model;
using Honua.Core.Features.Observability.Abstractions;

namespace Honua.Cloud.Aws.Features.Monitoring;

internal sealed record AwsOpsWatchdogMode(string ParameterName, string ResumeMode);

internal sealed class AwsOpsWatchdogKillSwitch(
    Func<PutParameterRequest, CancellationToken, Task<PutParameterResponse>> putParameter,
    AwsOpsWatchdogMode mode) : IOpsWatchdogKillSwitch
{
    public Task SuspendAsync(CancellationToken cancellationToken) => SetModeAsync("Off", cancellationToken);

    public Task ResumeAsync(CancellationToken cancellationToken) => SetModeAsync(mode.ResumeMode, cancellationToken);

    private async Task SetModeAsync(string value, CancellationToken cancellationToken)
    {
        try
        {
            await putParameter(new PutParameterRequest
            {
                Name = mode.ParameterName,
                Value = value,
                Type = Amazon.SimpleSystemsManagement.ParameterType.String,
                Overwrite = true,
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException
            or HttpRequestException or OperationCanceledException)
        {
            throw new IOException("The watchdog mode could not be updated.", exception);
        }
    }
}
