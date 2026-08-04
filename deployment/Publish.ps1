# Copyright (c) Microsoft Corporation. All rights reserved.
# Licensed under the MIT License. See LICENSE file in the project root for license information.

#
# Publishes the Admin and Customer portal web apps to existing Azure App Service instances.
#

#.\Publish.ps1 `
# -WebAppNamePrefix "your-prefix" `
# -ResourceGroupForDeployment "your-resource-group"

Param(
   [string][Parameter(Mandatory)]$WebAppNamePrefix,          # Prefix used for the web applications (same as used in Deploy.ps1)
   [string][Parameter()]$ResourceGroupForDeployment,         # Name of the resource group
   [switch][Parameter()]$AdminOnly,                          # Only publish the Admin portal
   [switch][Parameter()]$CustomerOnly,                       # Only publish the Customer portal
   [switch][Parameter()]$Quiet                               # If set, only show error/warning output
)

$azCliOutput = if ($Quiet) { "none" } else { "json" }

# Known web-app-prefix -> resource-group mappings.
# Add more entries here as further environments are stood up.
$PrefixToResourceGroup = @{
    'rau' = 'rau-saas-commerial-marketplace-accelerator-dev'
}

if ($ResourceGroupForDeployment -eq "") {
    if ($PrefixToResourceGroup.ContainsKey($WebAppNamePrefix)) {
        $ResourceGroupForDeployment = $PrefixToResourceGroup[$WebAppNamePrefix]
    } else {
        $ResourceGroupForDeployment = $WebAppNamePrefix
    }
}

Write-Host ("   ->> Target resource group: {0}" -f $ResourceGroupForDeployment)

$WebAppNameAdmin  = $WebAppNamePrefix + "-admin"
$WebAppNamePortal = $WebAppNamePrefix + "-portal"

# Clean previous publish output
if (Test-Path '../Publish') {
    Remove-Item -Recurse -Force '../Publish'
}

if (-not $CustomerOnly) {
    Write-Host "   ->> Preparing Admin Site"
    dotnet publish ../src/AdminSite/AdminSite.csproj -c release -o ../Publish/AdminSite/ -v q

    # MeteredTriggerJob (the metered-billing WebJob) is RETIRED -- metering is now
    # emitted by the RauMetering function apps via the UsageLedger. Deliberately no
    # longer published so the IsMeteredBillingEnabled flag cannot resurrect it.
    # If the folder still exists on the web app, delete it via Kudu:
    #   site/wwwroot/app_data/jobs/triggered/MeteredTriggerJob

    Write-Host "   ->> Zipping Admin Site"
    Compress-Archive -Path ../Publish/AdminSite/* -DestinationPath ../Publish/AdminSite.zip -Force
}

if (-not $AdminOnly) {
    Write-Host "   ->> Preparing Customer Site"
    dotnet publish ../src/CustomerSite/CustomerSite.csproj -c release -o ../Publish/CustomerSite/ -v q

    Write-Host "   ->> Zipping Customer Site"
    Compress-Archive -Path ../Publish/CustomerSite/* -DestinationPath ../Publish/CustomerSite.zip -Force
}

if (-not $CustomerOnly) {
    Write-Host "   ->> Deploy Admin Portal"
    az webapp deploy --resource-group $ResourceGroupForDeployment --name $WebAppNameAdmin --src-path "../Publish/AdminSite.zip" --type zip --output $azCliOutput
}

if (-not $AdminOnly) {
    Write-Host "   ->> Deploy Customer Portal"
    az webapp deploy --resource-group $ResourceGroupForDeployment --name $WebAppNamePortal --src-path "../Publish/CustomerSite.zip" --type zip --output $azCliOutput
}

Write-Host "Publish complete."
