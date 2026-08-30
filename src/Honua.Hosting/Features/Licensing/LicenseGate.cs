// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Infrastructure.Models;
using Grpc.Core;

namespace Honua.Infrastructure.Licensing;

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

    /// <summary>
    /// Allocation-free live entitlement probe for per-request middleware predicates. Reads the
    /// current <see cref="ILicenseEntitlementService"/> snapshot directly — including the lazy
    /// expiration transition <c>FileBackedLicenseService.GetSnapshot</c> performs — so a license
    /// applied or expired after startup takes effect on the next request. Unlike
    /// <see cref="CheckEntitlement"/> it never composes the operator-facing upgrade message,
    /// which would allocate a string on every request of an unentitled deployment.
    /// </summary>
    internal static bool HasLiveEntitlement(IServiceProvider services, string entitlementKey)
    {
        var entitlementService = services.GetService<ILicenseEntitlementService>();
        return entitlementService is not null
            ? entitlementService.GetSnapshot().HasEntitlement(entitlementKey)
            : CheckEntitlement(services, entitlementKey).IsActive;
    }

    internal static LicenseEntitlementDecision CheckEntitlement(
        IServiceProvider services,
        string entitlementKey)
    {
        var capability = CapabilityKeyCatalog.All.FirstOrDefault(
            item => string.Equals(item.Key, entitlementKey, StringComparison.OrdinalIgnoreCase));

        var entitlementService = services.GetService<ILicenseEntitlementService>();
        if (entitlementService is not null)
        {
            var decision = entitlementService.CheckEntitlement(entitlementKey);
            if (decision.IsActive || capability is null || decision.Edition < capability.Edition)
            {
                return decision;
            }

            // Routed experimental capabilities live in CapabilityKeyCatalog rather than
            // FeatureCatalog so their maturity stays independent of deployability. Older
            // entitlement providers derive their edition grants from FeatureCatalog; honor
            // the canonical capability edition here until those providers consume the
            // broader catalog directly.
            return decision with
            {
                IsActive = true,
                RequiredEdition = capability.Edition,
                UpgradeMessage = string.Empty,
            };
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
        var requiredEdition = capability?.Edition;
        var active = status.Entitlements?.Any(
                entitlement => entitlement.IsActive &&
                    string.Equals(entitlement.Key, entitlementKey, StringComparison.OrdinalIgnoreCase)) == true ||
            (requiredEdition.HasValue && status.Edition >= requiredEdition.Value);

        var featureName = capability?.DisplayName ?? entitlementKey;
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
