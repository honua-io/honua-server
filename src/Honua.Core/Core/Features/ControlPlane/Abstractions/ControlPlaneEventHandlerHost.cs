// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Configuration;

namespace Honua.Core.Features.ControlPlane.Abstractions;

public static class ControlPlaneEventHandlerHost
{
    public const string HandlerEnvironmentVariable = "HONUA_CONTROL_PLANE_LAMBDA_HANDLER";

    public const string LambdaFunctionNameVariable = "AWS_LAMBDA_FUNCTION_NAME";

    public static bool IsEventHandler(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[HandlerEnvironmentVariable]);
    }

    public static bool IsLambda(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return !string.IsNullOrWhiteSpace(configuration[LambdaFunctionNameVariable])
            || IsEventHandler(configuration);
    }
}
