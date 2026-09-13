using System;
using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.Services.Utilities;

/// <summary>
/// Gates CUSTOMER-SITE auto-activation of marketplace free trials against repeat-trial abuse.
///
/// The marketplace itself grants (or withholds) the trial: a subscription arrives with
/// isFreeTrial = true. The loophole is cancel-and-resubscribe -- a customer who consumed a full
/// trial, unsubscribed (or let it expire) and purchases a fresh trial would otherwise be
/// auto-activated for another 30 days. This guard sends such repeats to MANUAL activation in
/// the publisher portal instead (they stay PendingFulfillmentStart); it never rejects them.
///
/// Rule: block auto-activation of a new free trial when the same purchaser has a prior,
/// now-terminated subscription that (a) was activated more than <c>retryWindowDays</c> ago
/// (default 37 = 30-day trial + 7-day grace, i.e. it consumed a full trial window) and
/// (b) ended within the last <c>cooldownDays</c> (default 365).
///
/// The retry allowance falls out of (a): a trial cancelled early and re-purchased shows a prior
/// activation LESS than 37 days old, so re-subscribes keep auto-activating for 37 calendar days
/// from the first activation -- "multiple attempts to keep a free trial running" stay
/// self-service. Deliberately keyed on ANY prior terminated subscription, not just prior
/// trials: legacy rows carry no IsFreeTrial flag, and churned-paid-then-trial also warrants a
/// publisher look. Paid (non-trial) new subscriptions are never blocked.
/// </summary>
public static class FreeTrialGuard
{
    /// <summary>Default retry window: 30-day marketplace trial + 7-day grace.</summary>
    public const int DefaultRetryWindowDays = 37;

    /// <summary>Default cooldown after a consumed trial: 12 months.</summary>
    public const int DefaultCooldownDays = 365;

    /// <summary>Statuses that mean the subscription is gone or on its way out.</summary>
    private static readonly string[] TerminatedStatuses =
    {
        "Unsubscribed",
        "PendingUnsubscribe",
        "UnsubscribeFailed",
    };

    /// <summary>
    /// True when auto-activation of a new FREE-TRIAL subscription must be blocked and routed to
    /// manual publisher activation. Always false for non-trial subscriptions.
    /// </summary>
    /// <param name="newSubscriptionIsFreeTrial">Whether the incoming subscription is a free trial (live marketplace value).</param>
    /// <param name="priorSubscriptions">The purchaser's other subscriptions, any status (the incoming one excluded).</param>
    /// <param name="utcNow">The current UTC time.</param>
    /// <param name="retryWindowDays">Days from a prior activation within which a re-subscribe still auto-activates.</param>
    /// <param name="cooldownDays">Days after a consumed prior subscription ends during which new trials need manual review.</param>
    public static bool BlocksAutoActivation(
        bool newSubscriptionIsFreeTrial,
        IEnumerable<Subscriptions> priorSubscriptions,
        DateTime utcNow,
        int retryWindowDays = DefaultRetryWindowDays,
        int cooldownDays = DefaultCooldownDays)
    {
        if (!newSubscriptionIsFreeTrial || priorSubscriptions == null)
        {
            return false;
        }

        return priorSubscriptions.Any(prior =>
            IsTerminated(prior, utcNow)
            && ConsumedTrialWindow(prior, utcNow, retryWindowDays)
            && EndedWithinCooldown(prior, utcNow, cooldownDays));
    }

    /// <summary>True when the prior subscription is unsubscribed/deleted or its term has expired.</summary>
    private static bool IsTerminated(Subscriptions prior, DateTime utcNow)
    {
        if (TerminatedStatuses.Any(s => string.Equals(s, prior.SubscriptionStatus, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return prior.EndDate.HasValue && prior.EndDate.Value < utcNow;
    }

    /// <summary>
    /// True when the prior subscription's activation is older than the retry window -- it had a
    /// full trial's worth of calendar time, so a new trial is a repeat rather than a retry.
    /// Priors with no usable date fail SAFE (treated as consumed).
    /// </summary>
    private static bool ConsumedTrialWindow(Subscriptions prior, DateTime utcNow, int retryWindowDays)
    {
        var activated = prior.StartDate ?? prior.CreateDate;
        if (!activated.HasValue)
        {
            return true;
        }

        return activated.Value <= utcNow.AddDays(-retryWindowDays);
    }

    /// <summary>
    /// True when the prior subscription ended recently enough to still count against the
    /// purchaser. Priors with no usable date fail SAFE (treated as recent).
    /// </summary>
    private static bool EndedWithinCooldown(Subscriptions prior, DateTime utcNow, int cooldownDays)
    {
        var ended = prior.ModifyDate ?? prior.EndDate;
        if (!ended.HasValue)
        {
            return true;
        }

        return ended.Value >= utcNow.AddDays(-cooldownDays);
    }
}
