using System.Collections.Generic;
using System.Linq;
using Marketplace.SaaS.Accelerator.DataAccess.Entities;

namespace Marketplace.SaaS.Accelerator.Services.Utilities;

/// <summary>
/// Gates CUSTOMER-SITE auto-activation of marketplace free trials against repeat trials.
///
/// The marketplace itself grants (or withholds) the trial: a subscription arrives with
/// isFreeTrial = true. The loophole is cancel-and-resubscribe -- a customer who took a trial,
/// unsubscribed (or let it expire) and purchases a fresh trial would otherwise be auto-activated
/// for another trial period. This guard sends such repeats to MANUAL activation in the publisher
/// portal instead (they stay PendingFulfillmentStart); it never rejects them.
///
/// Rule: one free trial per purchaser, ever. Block auto-activation of a new free trial when the
/// same purchaser has ANY prior subscription that was a free trial, whatever its status and
/// however long ago it ran. Priors whose trial flag is unknown (legacy rows with no IsFreeTrial
/// value) fail SAFE and also block. Prior paid (non-trial) subscriptions do not block, and paid
/// new subscriptions are never blocked.
/// </summary>
public static class FreeTrialGuard
{
    /// <summary>
    /// True when auto-activation of a new FREE-TRIAL subscription must be blocked and routed to
    /// manual publisher activation. Always false for non-trial subscriptions.
    /// </summary>
    /// <param name="newSubscriptionIsFreeTrial">Whether the incoming subscription is a free trial (live marketplace value).</param>
    /// <param name="priorSubscriptions">The purchaser's other subscriptions, any status (the incoming one excluded).</param>
    public static bool BlocksAutoActivation(
        bool newSubscriptionIsFreeTrial,
        IEnumerable<Subscriptions> priorSubscriptions)
    {
        if (!newSubscriptionIsFreeTrial || priorSubscriptions == null)
        {
            return false;
        }

        return priorSubscriptions.Any(prior => prior.IsFreeTrial != false);
    }
}
