// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Features.ControlPlane.Abstractions;

/// <summary>
/// Identifies the AWS Lambda processes that run this server image. The control-plane event
/// functions (reconcile, backstop, scheduled tick) are a separate process from the API function:
/// honua-iac sets <see cref="HandlerEnvironmentVariable"/> on those three only.
/// </summary>
public static class ControlPlaneEventHandlerHost
{
    /// <summary>
    /// Environment variable that selects the control-plane event handler
    /// (<c>batch-event</c>, <c>backstop</c>, or <c>scheduled-tick</c>).
    /// </summary>
    public const string HandlerEnvironmentVariable = "HONUA_CONTROL_PLANE_LAMBDA_HANDLER";

    /// <summary>Set by the AWS Lambda runtime on every function, including the API function.</summary>
    public const string LambdaFunctionNameVariable = "AWS_LAMBDA_FUNCTION_NAME";

    /// <summary>
    /// Whether this process is a control-plane event-handler Lambda. It serves one EventBridge
    /// invocation at a time and is frozen in between, so it must not host database polling loops.
    /// </summary>
    public static bool IsEventHandler(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[HandlerEnvironmentVariable]);
    }

    /// <summary>
    /// Whether this process is any AWS Lambda function (the API behind the web adapter, or a
    /// control-plane event handler). A frozen instance does not prune its Npgsql pool, so the
    /// pool floor has to be explicit rather than the long-lived default.
    /// </summary>
    public static bool IsLambda(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[LambdaFunctionNameVariable])
            || IsEventHandler(configuration);
    }
}
