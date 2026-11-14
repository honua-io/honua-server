// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon;
using Amazon.SQS;
using Amazon.SQS.Model;
using Honua.Core.Features.Observability.Abstractions;
using Honua.Core.Features.Observability.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Infrastructure.Monitoring;

internal sealed class AwsOpsEventQueueClient(IAmazonSQS client, string queueUrl) : IOpsEventQueueClient, IDisposable
{
    public async Task<IReadOnlyList<OpsEventQueueMessage>> ReceiveAsync(CancellationToken cancellationToken)
    {
        var response = await client.ReceiveMessageAsync(new ReceiveMessageRequest
        {
            QueueUrl = queueUrl,
            WaitTimeSeconds = 20,
            MaxNumberOfMessages = 10,
            VisibilityTimeout = 120,
        }, cancellationToken).ConfigureAwait(false);
        return response.Messages?.Select(m => new OpsEventQueueMessage(m.ReceiptHandle, m.Body)).ToArray() ?? [];
    }

    public async Task DeleteAsync(string receiptHandle, CancellationToken cancellationToken)
    {
        await client.DeleteMessageAsync(new DeleteMessageRequest
        {
            QueueUrl = queueUrl,
            ReceiptHandle = receiptHandle,
        }, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => client.Dispose();
}

internal static class AwsOpsEventQueueRegistration
{
    internal static bool AddAwsOpsEventQueue(this IServiceCollection services, IConfiguration configuration)
    {
        var section = configuration.GetSection("Observability:OpsEvents");
        var queueUrl = section["QueueUrl"];
        if (string.IsNullOrWhiteSpace(queueUrl))
        {
            return false;
        }

        services.AddSingleton<IOpsEventQueueClient>(_ =>
        {
            var config = new AmazonSQSConfig { Timeout = TimeSpan.FromSeconds(40) };
            if (section["Region"] is { Length: > 0 } region)
            {
                config.RegionEndpoint = RegionEndpoint.GetBySystemName(region);
            }

            return new AwsOpsEventQueueClient(new AmazonSQSClient(config), queueUrl);
        });
        return true;
    }
}
