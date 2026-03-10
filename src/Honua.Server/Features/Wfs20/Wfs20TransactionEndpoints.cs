// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Server.Features.Infrastructure.Models;
using Honua.Server.Features.Wfs20.Models;
using Microsoft.AspNetCore.Mvc;

namespace Honua.Server.Features.Wfs20;

/// <summary>
/// WFS 2.0 Transaction endpoints (WFS-T)
/// </summary>
internal static class Wfs20TransactionEndpoints
{
    /// <summary>
    /// Maps WFS 2.0 Transaction endpoints
    /// </summary>
    public static IEndpointRouteBuilder MapWfs20TransactionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var transaction = endpoints.MapPost("/wfs", HandleTransaction)
            .WithDisplayName("WFS 2.0 Transaction")
            .WithName("Wfs20Transaction")
            .WithSummary("Process WFS 2.0 transaction operations")
            .WithDescription("Handle Insert, Update, and Delete operations using WFS 2.0 Transaction")
            .WithTags("WFS 2.0")
            .Produces<string>(200, "application/xml")
            .Produces<ExceptionReport>(400, "application/xml")
            .Produces(500);

        return endpoints;
    }

    /// <summary>
    /// Handles WFS 2.0 Transaction requests
    /// </summary>
    private static async Task<IResult> HandleTransaction(
        HttpContext context,
        [FromBody] string? transactionXml,
        [FromServices] ILogger<Wfs20Endpoints.Wfs20EndpointsLog> logger)
    {
        // Check if this is a Transaction request
        var request = context.Request.Query[Wfs20Utilities.ParameterNames.Request].FirstOrDefault();
        var isGetRequest = context.Request.Method == "GET";

        if (isGetRequest && !string.Equals(request, Wfs20Utilities.Operations.Transaction, StringComparison.OrdinalIgnoreCase))
        {
            return Results.BadRequest("Not a Transaction request");
        }

        try
        {
            // Read transaction XML from request body if not provided
            if (string.IsNullOrEmpty(transactionXml) && context.Request.HasFormContentType == false)
            {
                using var reader = new StreamReader(context.Request.Body);
                transactionXml = await reader.ReadToEndAsync();
            }

            if (string.IsNullOrEmpty(transactionXml))
            {
                return CreateExceptionResponse("MissingParameterValue", "Transaction request body is required", null);
            }

            // Parse and validate transaction XML
            var transactionRequest = await ParseTransactionRequest(transactionXml);

            Wfs20Log.TransactionRequested(logger,
                transactionRequest.InsertCount,
                transactionRequest.UpdateCount,
                transactionRequest.DeleteCount);

            // Process transaction operations
            var transactionResponse = await ProcessTransaction(transactionRequest);

            Wfs20Log.TransactionReturned(logger,
                transactionResponse.Inserted,
                transactionResponse.Updated,
                transactionResponse.Deleted);

            return Results.Content(SerializeTransactionResponse(transactionResponse), "application/xml");
        }
        catch (TransactionParseException ex)
        {
            Wfs20Log.FilterParsingFailed(logger, "Transaction", ex.Message);
            return CreateExceptionResponse("InvalidParameterValue", $"Invalid transaction XML: {ex.Message}", null);
        }
        catch (Exception ex)
        {
            Wfs20Log.DatabaseQueryFailed(logger, "Transaction", ex.Message);
            return CreateExceptionResponse("NoApplicableCode", $"Transaction failed: {ex.Message}", null);
        }
    }

    /// <summary>
    /// Parses the transaction XML request
    /// </summary>
    private static async Task<TransactionRequest> ParseTransactionRequest(string transactionXml)
    {
        await Task.Delay(1); // Placeholder for async operation

        // TODO: Implement transaction XML parsing
        // This would involve:
        // 1. Parse the transaction XML document
        // 2. Extract Insert, Update, and Delete operations
        // 3. Validate operation structure and parameters
        // 4. Return structured representation

        return new TransactionRequest
        {
            InsertCount = 0,
            UpdateCount = 0,
            DeleteCount = 0,
            Operations = new List<TransactionOperation>()
        };
    }

    /// <summary>
    /// Processes the transaction operations
    /// </summary>
    private static async Task<TransactionResponse> ProcessTransaction(TransactionRequest request)
    {
        await Task.Delay(1); // Placeholder for async operation

        // TODO: Implement transaction processing
        // This would involve:
        // 1. Begin database transaction
        // 2. Process each operation (Insert/Update/Delete) in order
        // 3. Validate data and permissions
        // 4. Apply changes to feature store
        // 5. Commit or rollback transaction
        // 6. Generate response with operation results

        return new TransactionResponse
        {
            Inserted = 0,
            Updated = 0,
            Deleted = 0,
            InsertResults = new List<string>(),
            Success = true
        };
    }

    /// <summary>
    /// Serializes transaction response to XML
    /// </summary>
    private static string SerializeTransactionResponse(TransactionResponse response)
    {
        var insertResults = string.Join("\n", response.InsertResults.Select(id =>
            $"        <wfs:Feature><ogc:FeatureId fid=\"{id}\"/></wfs:Feature>"));

        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <wfs:TransactionResponse
                xmlns:wfs="http://www.opengis.net/wfs/2.0"
                xmlns:ogc="http://www.opengis.net/ogc"
                xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance"
                version="2.0.0">

                <wfs:TransactionSummary>
                    <wfs:totalInserted>{response.Inserted}</wfs:totalInserted>
                    <wfs:totalUpdated>{response.Updated}</wfs:totalUpdated>
                    <wfs:totalDeleted>{response.Deleted}</wfs:totalDeleted>
                </wfs:TransactionSummary>

                {(response.InsertResults.Count > 0 ? $"""
                <wfs:InsertResults>
                {insertResults}
                </wfs:InsertResults>
                """ : "")}

            </wfs:TransactionResponse>
            """;
    }

    /// <summary>
    /// Creates a WFS exception response
    /// </summary>
    private static IResult CreateExceptionResponse(string exceptionCode, string exceptionText, string? locator)
    {
        var xmlContent = $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <ows:ExceptionReport xmlns:ows="http://www.opengis.net/ows/1.1" version="1.1.0">
                <ows:Exception exceptionCode="{exceptionCode}" {(locator != null ? $"locator=\"{locator}\"" : "")}>
                    {exceptionText}
                </ows:Exception>
            </ows:ExceptionReport>
            """;

        return Results.BadRequest(Results.Content(xmlContent, "application/xml"));
    }
}

/// <summary>
/// Represents a parsed transaction request
/// </summary>
internal sealed class TransactionRequest
{
    public int InsertCount { get; set; }
    public int UpdateCount { get; set; }
    public int DeleteCount { get; set; }
    public required List<TransactionOperation> Operations { get; set; }
}

/// <summary>
/// Represents a transaction operation
/// </summary>
internal sealed class TransactionOperation
{
    public required string Type { get; set; } // Insert, Update, Delete
    public required string TypeName { get; set; }
    public string? Filter { get; set; }
    public string? FeatureData { get; set; }
}

/// <summary>
/// Represents a transaction response
/// </summary>
internal sealed class TransactionResponse
{
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public int Deleted { get; set; }
    public required List<string> InsertResults { get; set; }
    public bool Success { get; set; }
}

/// <summary>
/// Exception thrown when transaction XML parsing fails
/// </summary>
public sealed class TransactionParseException : Exception
{
    public TransactionParseException(string message) : base(message) { }
    public TransactionParseException(string message, Exception innerException) : base(message, innerException) { }
}
