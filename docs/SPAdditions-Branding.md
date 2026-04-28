# SP Additions / Read and Understood — Branding Layer

A fork-safe theme of the upstream SaaS Accelerator landing experience for **SP Additions Ltd**'s *Read and Understood* product on Microsoft Marketplace.

The goal of this layer: make pulling future upstream releases as low-friction as possible. New brand visuals live in **new files** the upstream doesn't touch; edits to upstream files are kept to small, easily-rebased hunks.

## What this page is (and isn't)

The CustomerSite "landing page" is **not** a marketing page — by the time a customer reaches it, they have already discovered, chosen and purchased the product through Microsoft Marketplace. They've clicked **Configure account** on their subscription tile and been redirected here with an activation token.

So the page has two real states:

1. **No token / unauthenticated** (what `_BrandLanding.cshtml` renders) — a branded welcome panel that helps the user sign in, or recover the activation flow if they arrived here without a token. **No "Get it on Marketplace" CTA** — they're already past that step.
2. **With token** (the existing upstream subscription-detail form) — confirms what they bought, captures any required parameters, and lets them activate. We brand this without restructuring it: the upstream Razor markup stays intact, and a `brand-activation` scope class on the outer container opts in to brand-themed CSS overrides of the existing `cm-` classes.

---

## Files at a glance

### New (zero upstream conflict surface)

| Path | Purpose |
|---|---|
| `src/CustomerSite/wwwroot/brand/sp-additions-logo.png` | Brand logo (used in nav and hero) |
| `src/CustomerSite/wwwroot/brand/favicon.ico` | Browser tab icon |
| `src/CustomerSite/Views/Home/_BrandLanding.cshtml` | No-token welcome partial: hero + sign-in card + recovery help |
| `docs/SPAdditions-Branding.md` | This doc |

### Edited (small, contained changes)

| Path | Change |
|---|---|
| `src/CustomerSite/appsettings.json` | Added `Brand` section (logo path, welcome/activation copy, support details, optional Adobe Fonts kit ID) |
| `src/CustomerSite/Views/Home/_LandingPage.cshtml` | Two single-token edits: added `brand-activation` class to the outer `divHome` container (to scope CSS to the activation form), and replaced the upstream `else { … "Welcome" card … }` block with `@await Html.PartialAsync("_BrandLanding")` |
| `src/CustomerSite/Views/Shared/_Layout.cshtml` | Added `@inject IConfiguration`, config-driven `<title>` / favicon / logo / footer, Montserrat + optional Typekit font links, `brand-nav` and `brand-footer` classes |
| `src/CustomerSite/wwwroot/css/customer-custom.css` | Appended a clearly-marked brand layer. The `brand-` prefixed rules are entirely additive; the `.brand-activation .cm-…` rules are scoped overrides that only apply when the brand-activation ancestor is present — no existing rules were modified |

> All new CSS classes use the `brand-` prefix to avoid colliding with the upstream `cm-` classes or Bootstrap.

---

## Brand tokens

Defined as CSS custom properties at the top of the brand layer in `customer-custom.css`. To re-theme, change values in **one place** — every component picks them up.

| Token | Value | Role |
|---|---|---|
| `--brand-green-500` | `#7ca22b` | Olive primary (the "P" in the logo) |
| `--brand-green-600` | `#6d8e26` | Hover / active green |
| `--brand-green-100` | `#eaf3d4` | Tinted icon backgrounds |
| `--brand-burgundy-500` | `#8a292e` | Brand burgundy (the "S/Additions" wordmark) |
| `--brand-burgundy-600` | `#6f1f23` | Hover / active burgundy |
| `--brand-burgundy-100` | `#f5e1e2` | Tinted icon / pill backgrounds |
| `--brand-purple-500` | `#7b5394` | Optional accent (kept reserved for now) |
| `--brand-ink` | `#1f1f23` | Headings / body |
| `--brand-bg` | `#faf8f5` | Page surface (warm off-white) |
| `--brand-surface` | `#ffffff` | Card surface |
| `--brand-line` | `#e7e6e3` | Hairline borders |

---

## Where to edit what

### Change copy, CTAs, support email
Edit `src/CustomerSite/appsettings.json` → `Brand` section. No code changes needed.

```jsonc
"Brand": {
  "ProductName": "Read and Understood",
  "ProductTagline": "...",
  "Headline": "...",
  "SubHeadline": "...",
  "PrimaryCtaText": "Get it on Microsoft Marketplace",
  "PrimaryCtaUrl": "https://marketplace.microsoft.com/en-us/product/WA200007564",
  "SecondaryCtaText": "Learn more",
  "SecondaryCtaUrl": "https://readandunderstood.com/",
  "SupportEmail": "support@spadditions.zendesk.com",
  "CompanyAddress": "27 Old Gloucester Street, London, WC1N 3AF, United Kingdom",
  "AdobeFontsKitId": ""
}
```

The same section is read by `_Layout.cshtml` (title, logo, favicon, footer) and `_BrandLanding.cshtml` (everything else).

### Change feature cards / structure
Edit `src/CustomerSite/Views/Home/_BrandLanding.cshtml` — it's a normal Razor partial.

### Change colours, spacing, type
Edit the brand layer at the bottom of `src/CustomerSite/wwwroot/css/customer-custom.css` (everything below the comment banner *"SP Additions / Read and Understood brand layer"*).

### Replace logo / favicon
Drop new files into `src/CustomerSite/wwwroot/brand/` and update `Brand:LogoPath` / `Brand:FaviconPath` in `appsettings.json` if you change filenames.

---

## Typography: getting true Proxima Nova

Day one, the page renders in **Montserrat** (Google Fonts, free, geometrically near-identical to Proxima Nova). To swap to true Proxima Nova:

1. Sign in to [fonts.adobe.com](https://fonts.adobe.com/) with your Adobe ID.
2. Create a Web Project containing **Proxima Nova** (Regular 400, Medium 500, Semibold 600, Bold 700).
3. Copy the **Project ID** (Adobe calls it a "kit ID" — looks like `abc1xyz`).
4. Set it in `appsettings.json` → `Brand:AdobeFontsKitId`.
5. Add your production hostname to the kit's allowed domains in fonts.adobe.com.

The brand `font-family` stack is:
`"proxima-nova", "Proxima Nova", "Montserrat", "Segoe UI", system-ui, …`

…so the kit's `proxima-nova` family wins the moment Adobe serves it.

---

## Upstream merge guidance

When pulling a new release of the SaaS Accelerator fork, the only files likely to need attention are:

| File | Likelihood of conflict | Resolution |
|---|---|---|
| `Views/Home/_LandingPage.cshtml` | Low — the welcome `else` block is rarely touched | Keep our `@await Html.PartialAsync("_BrandLanding")` in the `else` branch |
| `Views/Shared/_Layout.cshtml` | Medium — chrome can change | Keep our `@inject` block at the top, the `brand-nav` / `brand-footer` class additions, the config-driven title/favicon/logo/footer text, and font links |
| `wwwroot/css/customer-custom.css` | Low — we only **appended**, never edited upstream rules | Keep everything below the *"SP Additions / Read and Understood brand layer"* banner |
| `appsettings.json` | Low — we only added a new top-level `Brand` key | Keep the `Brand` section |

Everything else (the `_BrandLanding.cshtml` partial, the `wwwroot/brand/` folder, this doc) is fork-only and won't conflict.

---

## Scope

**Done:** the unauthenticated welcome screen at `/` (when `Model.ShowWelcomeScreen == true`) and the shared layout chrome (nav, title, favicon, footer) for **CustomerSite**.

**Not in scope (yet):**
- AdminSite branding — the publisher portal still ships in upstream styling
- Authenticated subscription/management pages — they continue to use the upstream `cm-*` look (intentional: less surface area to maintain)
- Sign-in page styling beyond the layout chrome it inherits

If/when those are wanted, the same pattern applies: new partials + appended CSS + minimal layout edits.

---

## Quick local preview

```bash
dotnet run --project src/CustomerSite
```

Hit the site root unauthenticated to see the brand landing. Authenticated subscription views will continue to use the upstream styling.
