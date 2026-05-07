// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Server.Features.Infrastructure.Models;
using Grpc.Core;

namespace Honua.Server.Features.Infrastructure.Licensing;

internal static class LicenseGate
{
    internal static IResult? RequireEntitlement(
        HttpContext context,
        string entitlementKey,
        string featureName,
        ILogger? logger = null)
    {
        var decision = CheckEntitlement(context.RequestServices, entitlementKey);
        if (decision.IsActive)
        {
            return null;
        }

        if (logger is not null)
        {
            LicenseRuntimeLog.EntitlementDenied(
                logger,
                entitlementKey,
                featureName,
                decision.Edition,
                decision.ValidationState);
        }

        return StandardErrorHelpers.CreatePaymentRequired(
            context,
            decision.UpgradeMessage,
            [$"entitlement: {entitlementKey}"]);
    }

    internal static bool IsEntitlementActive(IServiceProvider services, string entitlementKey)
        => CheckEntitlement(services, entitlementKey).IsActive;

    internal static LicenseEntitlementDecision CheckEntitlement(
        IServiceProvider services,
        string entitlementKey)
    {
        var entitlementService = services.GetService<ILicenseEntitlementService>();
        if (entitlementService is not null)
        {
            return entitlementService.CheckEntitlement(entitlementKey);
        }

        var statusProvider = services.GetService<ILicenseStatusProvider>();
        if (statusProvider is null)
        {
            return new LicenseEntitlementDecision(
                entitlementKey,
                false,
                HonuaEdition.Community,
                LicenseValidationState.NoLicenseConfigured,
                null,
                $"Entitlement '{entitlementKey}' is not active.");
        }

        var status = statusProvider.GetCurrentStatus();
        var feature = FeatureCatalog.All.FirstOrDefault(
            item => string.Equals(item.Key, entitlementKey, StringComparison.OrdinalIgnoreCase));
        var requiredEdition = feature?.MinimumEdition;
        var active = status.Entitlements?.Any(
                entitlement => entitlement.IsActive &&
                    string.Equals(entitlement.Key, entitlementKey, StringComparison.OrdinalIgnoreCase)) == true ||
            (requiredEdition.HasValue && status.Edition >= requiredEdition.Value);

        var featureName = feature?.DisplayName ?? entitlementKey;
        var upgradeMessage = active
            ? string.Empty
            : $"{featureName} requires an active {requiredEdition?.ToString() ?? "paid"} entitlement. " +
                $"Current edition is {status.Edition}; install a license that includes '{entitlementKey}'.";

        return new LicenseEntitlementDecision(
            entitlementKey,
            active,
            status.Edition,
            status.ValidationState,
            requiredEdition,
            upgradeMessage);
    }

    internal static RpcException CreateFailedPreconditionRpcException(
        IServiceProvider services,
        string entitlementKey)
    {
        var decision = CheckEntitlement(services, entitlementKey);
        return new RpcException(new Status(StatusCode.FailedPrecondition, decision.UpgradeMessage));
    }
}
