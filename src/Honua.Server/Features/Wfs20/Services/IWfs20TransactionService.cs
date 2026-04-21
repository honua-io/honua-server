// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Wfs20.Services;

/// <summary>
/// Service responsible for handling WFS 2.0 transaction operations (insert, update, delete).
/// Segregated interface following the Interface Segregation Principle.
/// </summary>
internal interface IWfs20TransactionService
{
    /// <summary>
    /// Handle transaction requests (insert, update, delete operations)
    /// </summary>
    /// <param name="context">HTTP context for the request</param>
    /// <param name="transactionXml">Transaction XML content</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Transaction result</returns>
    Task<IResult> HandleTransactionAsync(
        HttpContext context,
        string transactionXml,
        CancellationToken cancellationToken = default);
}