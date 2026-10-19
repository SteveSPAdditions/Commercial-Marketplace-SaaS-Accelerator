# Marketplace API authentication — federated identity (Auth:Mode)

**Created:** 19 October 2026, UK local time

The three hosts in this repo that call the Marketplace Fulfillment / Metered Billing APIs
(`rau-admin` AdminSite, `rau-portal` CustomerSite, and the MeteredTriggerJob web job inside the
admin App Service) can authenticate as the fulfilment app registration in one of two ways,
selected by configuration — no rebuild:

| `Auth:Mode` | Credential |
|---|---|
| absent / `Certificate` (default) | the pre-existing client secret (`SaaSApiConfiguration:ClientSecret`), unchanged |
| `Federated` | the App Service's **user-assigned managed identity**, presented to Entra as a federated credential on the fulfilment app registration — no secret anywhere |

## Configuration keys

Read by `src\Services\Utilities\AppOnlyCredentialFactory.cs` (a manually-synced duplicate of the
factory in the main TFVC repo), precedence: environment variable (`Auth__Mode` and `Auth:Mode`
forms both accepted) → `IConfiguration` → legacy key → default.

| Key | Value |
|---|---|
| `Auth:Mode` | `Certificate` (default) \| `Federated` |
| `Auth:ManagedIdentityClientId` | client id of the user-assigned managed identity (required in Federated mode here) |
| `Auth:FulfilmentAppId` | fulfilment app registration id; falls back to `SaaSApiConfiguration:ClientId` |

On App Service these are application settings written in double-underscore form
(`Auth__Mode`), like the existing `SaaSApiConfiguration__*` settings in `deployment\Deploy.ps1`.

## Startup line

Each host logs one line at startup — the first thing to check during an incident:

```
Auth mode: Federated (identity 7b2a…c1, fulfilment=252c…38)
Auth mode: Certificate (legacy client secret, fulfilment=252c…38)
```

In Federated mode a host without a managed-identity endpoint **fails fast at startup** with an
`InvalidOperationException` naming `IDENTITY_ENDPOINT` — rollback is flipping `Auth__Mode` back
to `Certificate` (the App Service restarts automatically on a setting change).

## Azure-side prerequisites, rollout and rollback

Owned by *SP Additions Federated Identity Rollout Plan v1 draft* and
`Enable-FederatedIdentity-DEV.ps1` (repo `MarketPlace Listings`): user-assigned identity
creation and assignment to the App Services, the federated identity credential on the fulfilment
app registration, and the per-environment identity table (plan §2).

Local development has no managed identity: leave `Auth:Mode` absent and the client-secret path
runs exactly as before (user secrets / `appsettings.Development.json`).
