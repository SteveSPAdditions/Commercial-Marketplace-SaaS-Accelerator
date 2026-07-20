using System;

namespace Marketplace.SaaS.Accelerator.Services.Utilities;

/// <summary>
/// Normalizes the accelerator's internal subscription-status strings
/// (<see cref="Models.SubscriptionStatusEnumExtension"/>) to Microsoft Marketplace's canonical
/// values, so push-authoritative consumers (RAU signaling + reconcile snapshot) compare against
/// the same vocabulary RAU's live Fulfillment pull returns.
///
/// The only divergence today is the accelerator's <c>Suspend</c> (enum member name) vs
/// Microsoft's <c>Suspended</c>. All other values the producers emit (Subscribed / Unsubscribed /
/// PendingFulfillmentStart) already match, and unknown values pass through unchanged.
/// </summary>
public static class SubscriptionStatusNormalizer
{
    /// <summary>Map an accelerator status string to the Marketplace canonical value.</summary>
    public static string ToMarketplaceStatus(string status)
        => string.Equals(status, "Suspend", StringComparison.OrdinalIgnoreCase)
            ? "Suspended"
            : status;
}
