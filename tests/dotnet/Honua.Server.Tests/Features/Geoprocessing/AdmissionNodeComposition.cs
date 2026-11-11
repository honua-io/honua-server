// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Geoprocessing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace Honua.Server.Tests.Features.Geoprocessing;

// The admission stack one server node builds over its own Redis connection and job store, with
// operator authorization and approval that allow every request.
internal sealed class AdmissionNodeComposition
{
    private AdmissionNodeComposition(ExecutionAdmissionEvaluator evaluator, ExecutionAdmissionCoordinator coordinator)
    {
        Evaluator = evaluator;
        Coordinator = coordinator;

        OperatorAuthorization = Substitute.For<IOperatorAuthorizationEvaluator>();
        OperatorAuthorization
            .EvaluateAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AccessDecision.Allowed()));
        OperatorApproval = Substitute.For<IOperatorApprovalEvaluator>();
        OperatorApproval
            .Evaluate(Arg.Any<ClaimsPrincipal>(), Arg.Any<OperatorAuthorizationRequest>())
            .Returns(ApprovalRequirement.NotRequired());
    }

    public ExecutionAdmissionEvaluator Evaluator { get; }

    public ExecutionAdmissionCoordinator Coordinator { get; }

    public IOperatorAuthorizationEvaluator OperatorAuthorization { get; }

    public IOperatorApprovalEvaluator OperatorApproval { get; }

    public static AdmissionNodeComposition Compose(
        ConnectionMultiplexer multiplexer,
        IExecutionJobStore store,
        IOptionsMonitor<ExecutionAdmissionOptions> limits,
        bool sharedLease)
    {
        var evaluator = new ExecutionAdmissionEvaluator(
            limits,
            TimeProvider.System,
            NullLogger<ExecutionAdmissionEvaluator>.Instance,
            store,
            multiplexer);
        var coordinator = new ExecutionAdmissionCoordinator(
            limits,
            NullLogger<ExecutionAdmissionCoordinator>.Instance,
            sharedLease ? multiplexer : null);
        return new AdmissionNodeComposition(evaluator, coordinator);
    }
}
