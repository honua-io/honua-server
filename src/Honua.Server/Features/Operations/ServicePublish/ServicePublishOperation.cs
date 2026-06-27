// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Admin.Abstractions;
using Honua.Core.Features.Admin.Domain;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Security.Abstractions;

namespace Honua.Server.Features.Operations.ServicePublish;

/// <summary>
/// The <c>service.publish</c> operation. Adapts an operation invocation onto the
/// canonical <see cref="ILayerPublishingService"/> — it maps the operation input to a
/// <see cref="LayerPublishRequest"/>, publishes, and maps the
/// <see cref="PublishedLayerSummary"/> back to an <see cref="OperationResult"/>. It
/// does not reimplement publish behaviour.
/// </summary>
internal sealed class ServicePublishOperation(
    ILayerPublishingService publishingService,
    ISecureConnectionResolver connectionResolver) : IHonuaOperation
{
    /// <summary>
    /// Catalogued identifier for this operation.
    /// </summary>
    public const string Id = "service.publish";

    public string OperationId => Id;

    public async Task<OperationResult> ExecuteAsync(
        OperationInvocationContext context,
        CancellationToken cancellationToken = default)
    {
        if (context.Input is not { } inputElement)
        {
            return OperationResult.Failed(Id, "operation.invalid_input", "A 'service.publish' input payload is required.");
        }

        ServicePublishInput? input;
        try
        {
            input = inputElement.Deserialize(OperationsJsonContext.Default.ServicePublishInput);
        }
        catch (JsonException)
        {
            return OperationResult.Failed(Id, "operation.invalid_input", "The 'service.publish' input payload is malformed.");
        }

        if (input is null
            || string.IsNullOrWhiteSpace(input.ConnectionId)
            || string.IsNullOrWhiteSpace(input.Schema)
            || string.IsNullOrWhiteSpace(input.Table)
            || string.IsNullOrWhiteSpace(input.LayerName))
        {
            return OperationResult.Failed(
                Id,
                "operation.invalid_input",
                "'connectionId', 'schema', 'table', and 'layerName' are required.");
        }

        try
        {
            var connectionString = await ResolveConnectionStringAsync(input.ConnectionId, cancellationToken)
                .ConfigureAwait(false);

            var connectionId = Guid.TryParse(input.ConnectionId, out var parsedId) ? parsedId : (Guid?)null;
            var publishRequest = new LayerPublishRequest
            {
                Schema = input.Schema,
                Table = input.Table,
                LayerName = input.LayerName,
                Description = input.Description,
                GeometryColumn = input.GeometryColumn,
                GeometryType = input.GeometryType,
                Srid = input.Srid,
                PrimaryKey = input.PrimaryKey,
                Fields = input.Fields,
                ServiceName = input.ServiceName,
                ConnectionId = connectionId,
                Enabled = input.Enabled
            };

            var summary = await publishingService
                .PublishLayerAsync(connectionString, publishRequest, cancellationToken)
                .ConfigureAwait(false);

            var output = new ServicePublishOutput
            {
                LayerId = summary.LayerId,
                LayerName = summary.LayerName,
                ServiceName = summary.ServiceName,
                Schema = summary.Schema,
                Table = summary.Table,
                GeometryType = summary.GeometryType,
                Srid = summary.Srid,
                FieldCount = summary.FieldCount,
                Enabled = summary.Enabled
            };

            var outputs = JsonSerializer.SerializeToElement(output, OperationsJsonContext.Default.ServicePublishOutput);
            return OperationResult.Succeeded(Id, outputs);
        }
        catch (LayerPublishingException ex)
        {
            var errorCode = ex.ErrorKind switch
            {
                LayerPublishingErrorKind.Conflict => "service.publish.conflict",
                LayerPublishingErrorKind.NotFound => "service.publish.not_found",
                LayerPublishingErrorKind.Validation => "service.publish.validation",
                _ => "service.publish.error"
            };

            return OperationResult.Failed(Id, errorCode, "The layer could not be published.");
        }
    }

    private async Task<string> ResolveConnectionStringAsync(string connectionId, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(connectionId, out var parsed))
        {
            return await connectionResolver.ResolveConnectionStringAsync(parsed, cancellationToken).ConfigureAwait(false);
        }

        return await connectionResolver.ResolveConnectionStringAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }
}
