// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Infrastructure.Models;

internal static class GeoServicesErrorCodes
{
    public const int BadRequest = 400;

    public const int Unauthorized = 401;

    public const int PaymentRequired = 402;

    public const int Forbidden = 403;

    public const int NotFound = 404;

    public const int MethodNotAllowed = 405;

    public const int RequestTimeout = 408;

    public const int PayloadTooLarge = 413;

    public const int TooManyRequests = 429;

    public const int UnprocessableEntity = 422;

    public const int InvalidToken = 498;

    public const int TokenRequired = 499;

    public const int Conflict = 409;

    public const int UnsupportedMediaType = 415;

    public const int InternalServerError = 500;

    // 501 is preserved as its own code so a client can tell an unsupported operation from a retryable fault or a validation failure.
    public const int NotImplemented = 501;

    public const int BadGateway = 502;

    public const int ServiceUnavailable = 503;

    public static int FromHttpStatusCode(int httpStatusCode) => httpStatusCode switch
    {
        400 => BadRequest,
        401 => Unauthorized,
        402 => PaymentRequired,
        403 => Forbidden,
        404 => NotFound,
        405 => MethodNotAllowed,
        408 => RequestTimeout,
        409 => Conflict,
        413 => PayloadTooLarge,
        415 => UnsupportedMediaType,
        429 => TooManyRequests,
        422 => UnprocessableEntity,
        498 => InvalidToken,
        499 => TokenRequired,
        500 => InternalServerError,
        // 501 passes through as a distinct "operation not supported" body code rather
        // than collapsing to the 500 default; see NotImplemented remarks (honua-server#2795).
        501 => NotImplemented,
        502 => BadGateway,
        503 => ServiceUnavailable,
        _ => InternalServerError // Default to 500 for unknown codes
    };
}
