# CLAUDE.md

## Project Overview

Commercial Marketplace SaaS Accelerator — a Microsoft open-source reference implementation for integrating SaaS applications with the Azure Commercial Marketplace. Provides subscription management, metered billing, and admin/customer portals.

## Tech Stack

- .NET 8 (SDK 8.0.303), ASP.NET Core MVC
- Entity Framework Core 8 with Azure SQL Database
- Azure AD / Entra ID (OpenID Connect) for authentication
- Microsoft Marketplace APIs (Fulfillment v2 + Metering)
- PowerShell deployment scripts for Azure provisioning

## Solution Structure

```
src/SaaSAccelerator.sln
├── AdminSite/          — Publisher portal (manage subscriptions, plans, offers, metered billing)
├── CustomerSite/       — Customer portal (activate, change plans, unsubscribe)
├── Services/           — Core business logic & Marketplace API integration
├── DataAccess/         — EF Core repositories, entities, migrations
├── MeteredTriggerJob/  — Background console app for scheduled metered billing
├── Services.Test/      — Unit tests (MSTest + Moq)
└── UI.Test/            — UI integration tests
```

## Build & Run

```bash
# Build the solution
dotnet build src/SaaSAccelerator.sln

# Run tests
dotnet test src/SaaSAccelerator.sln

# Run AdminSite locally
dotnet run --project src/AdminSite

# Run CustomerSite locally
dotnet run --project src/CustomerSite
```

## Deployment

```powershell
# Deploy to Azure (from deployment/ directory)
.\Deploy.ps1 -WebAppNamePrefix "prefix" -Location "region" -PublisherAdminUsers "email@example.com"
```

See [docs/Installation-Instructions.md](docs/Installation-Instructions.md) for full setup guide.

## Key Configuration

- App settings: `src/*/appsettings.json` (templates with empty placeholders)
- Dev settings: `src/*/appsettings.Development.json` (gitignored, contains real credentials locally)
- Azure AD config section: `SaaSApiConfiguration` in appsettings
- Config class: `src/Services/Configurations/SaaSApiClientConfiguration.cs`

## Important Conventions

- Database migrations are in `src/DataAccess/Migrations/`
- Subscription state machine handlers are in `src/Services/StatusHandlers/`
- Marketplace API resource ID (public): `20e940b3-4c77-4b0b-9a53-9e16a1b010a7`
- Never commit `appsettings.Development.json` files (they contain secrets)

## Documentation

- [docs/](docs/) — Installation, architecture, security, monitoring guides
- [deployment/](deployment/) — PowerShell deploy/upgrade scripts
- License: MIT
