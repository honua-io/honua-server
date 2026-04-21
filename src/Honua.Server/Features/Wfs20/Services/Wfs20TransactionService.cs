// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics;
using System.Xml.Linq;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.ServiceDefaults;

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Service responsible for WFS 2.0 transaction operations.
/// Follows Single Responsibility Principle by handling only transaction-related operations.
/// </summary>
internal sealed class Wfs20TransactionService : IWfs20TransactionService
{
    private readonly ILogger<Wfs20TransactionService> _logger;
    private readonly ILayerCatalog _layerCatalog;
    private readonly IFeatureWriter _featureWriter;
    private readonly IWfs20TransactionHandler _transactionHandler;

    public Wfs20TransactionService(
        ILogger<Wfs20TransactionService> logger,
        ILayerCatalog layerCatalog,
        IFeatureWriter featureWriter,
        IWfs20TransactionHandler transactionHandler)
    {
        _logger = logger;
        _layerCatalog = layerCatalog;
        _featureWriter = featureWriter;
        _transactionHandler = transactionHandler;
    }

    public async Task<IResult> HandleTransactionAsync(
        HttpContext context,
        string transactionXml,
        CancellationToken cancellationToken = default)
    {
        using var activity = HonuaTelemetry.ActivitySource.StartActivity(
            "wfs20.transaction", ActivityKind.Internal);
        activity?.SetTag(HonuaTelemetry.Tags.Protocol, HonuaTelemetry.Protocols.Wfs20);
        activity?.SetTag(HonuaTelemetry.Tags.Operation, "Transaction");

        Wfs20Log.TransactionRequested(_logger, 0, 0, 0); // TODO: Parse counts from XML

        try
        {
            // Parse and validate the transaction XML
            var transactionDocument = XDocument.Parse(transactionXml);
            var transactionResult = await _transactionHandler.ProcessTransactionAsync(
                transactionDocument, cancellationToken);

            // Build transaction response
            var responseXml = BuildTransactionResponse(transactionResult);

            Wfs20Log.TransactionReturned(_logger,
                transactionResult.TransactionSummary.TotalInserted,
                transactionResult.TransactionSummary.TotalUpdated,
                transactionResult.TransactionSummary.TotalDeleted);

            return Results.Text(responseXml, "application/xml");
        }
        catch (Exception ex)
        {
            activity?.SetTag(HonuaTelemetry.Tags.Error, "true");
            activity?.SetTag(HonuaTelemetry.Tags.ErrorMessage, ex.Message);

            // Return transaction error response
            var errorResponse = BuildTransactionErrorResponse(ex);
            return Results.BadRequest(errorResponse);
        }
    }

    private static string BuildTransactionResponse(Wfs20TransactionResponse result)
    {
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<wfs:TransactionResponse
    xmlns:wfs="http://www.opengis.net/wfs/2.0"
    xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
    xsi:schemaLocation="http://www.opengis.net/wfs/2.0 http://schemas.opengis.net/wfs/2.0/wfs.xsd"
    version="2.0.0">
    <wfs:TransactionSummary>
        <wfs:totalInserted>{result.TransactionSummary.TotalInserted}</wfs:totalInserted>
        <wfs:totalUpdated>{result.TransactionSummary.TotalUpdated}</wfs:totalUpdated>
        <wfs:totalDeleted>{result.TransactionSummary.TotalDeleted}</wfs:totalDeleted>
    </wfs:TransactionSummary>
    {BuildInsertResults(result.OperationResults)}
</wfs:TransactionResponse>
""";
    }

    private static string BuildInsertResults(IReadOnlyCollection<Wfs20OperationResult> operationResults)
    {
        var insertResults = operationResults.Where(r => r.OperationType == Wfs20OperationType.Insert).ToList();
        if (insertResults.Count == 0)
            return "";

        var results = string.Join("\n", insertResults.Select(r =>
            $"        <wfs:Feature><fes:ResourceId rid=\"{r.FeatureId}\"/></wfs:Feature>"));

        return $"""
    <wfs:InsertResults>
{results}
    </wfs:InsertResults>
""";
    }

    private static string BuildTransactionErrorResponse(Exception ex)
    {
        return $"""
<?xml version="1.0" encoding="UTF-8"?>
<ows:ExceptionReport
    xmlns:ows="http://www.opengis.net/ows/1.1"
    xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
    xsi:schemaLocation="http://www.opengis.net/ows/1.1 http://schemas.opengis.net/ows/1.1.0/owsExceptionReport.xsd"
    version="1.1.0">
    <ows:Exception exceptionCode="NoApplicableCode">
        <ows:ExceptionText>{ex.Message}</ows:ExceptionText>
    </ows:Exception>
</ows:ExceptionReport>
""";
    }
}

