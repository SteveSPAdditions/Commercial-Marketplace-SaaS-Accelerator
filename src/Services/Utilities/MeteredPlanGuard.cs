using System;
using System.Linq;

namespace Marketplace.SaaS.Accelerator.Services.Utilities;

/// <summary>
/// Classifies marketplace plan ids as public vs private-offer, and gates manual activation on
/// the metered user threshold (N) for private-offer plans.
///
/// Public plans are identified by the configured allowlist
/// (<see cref="Configurations.SaaSApiClientConfiguration.PublicPlanIds"/>) and carry no
/// threshold -- N = 0 by design, Partner Center holds "quantity included in base" = 0 on every
/// plan. Any plan id NOT on the allowlist is a private-offer plan: usage is emitted against the
/// private plan's own id, and its negotiated N MUST be captured at activation. Absent N on an
/// Enterprise customer means metered billing emits the full headcount instead of the excess --
/// an over-bill of orders of magnitude -- so activation is blocked rather than defaulting to 0.
///
/// Matching is exact and case-sensitive: the allowlist entries must match Partner Center
/// verbatim. A mismatch fails SAFE here (the plan classifies as private and activation demands
/// N) rather than silently billing a private-offer subscription with no threshold.
/// </summary>
public static class MeteredPlanGuard
{
    /// <summary>
    /// True when <paramref name="planId"/> appears in the comma-separated
    /// <paramref name="publicPlanIdsCsv"/> allowlist (entries trimmed, matched ordinal /
    /// case-sensitive). Null or empty plan ids classify as private (fail safe).
    /// </summary>
    public static bool IsPublicPlan(string planId, string publicPlanIdsCsv)
    {
        if (string.IsNullOrWhiteSpace(planId) || string.IsNullOrWhiteSpace(publicPlanIdsCsv))
        {
            return false;
        }

        return publicPlanIdsCsv
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Trim())
            .Any(p => string.Equals(p, planId, StringComparison.Ordinal));
    }

    /// <summary>True when the plan is a private-offer plan and therefore requires N.</summary>
    public static bool RequiresThreshold(string planId, string publicPlanIdsCsv)
        => !IsPublicPlan(planId, publicPlanIdsCsv);

    /// <summary>
    /// True when manual activation must be BLOCKED: the plan requires a metered user threshold
    /// and none (or a negative one) was supplied. Public plans never block; a threshold of 0 is
    /// a valid explicit value for a private plan.
    /// </summary>
    public static bool BlocksActivation(string planId, string publicPlanIdsCsv, int? meteredUserThreshold)
        => RequiresThreshold(planId, publicPlanIdsCsv)
           && (meteredUserThreshold is null || meteredUserThreshold.Value < 0);
}
