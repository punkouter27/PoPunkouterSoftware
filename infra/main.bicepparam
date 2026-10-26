using './main.bicep'

// Values match the live resources in resource group 'PoPunkouterSoftware' (West US 2).
param appName = 'app-popunkoutersoftware2'
// Resources live in West US 2, but the resource group's default location is
// East US 2 — pin it so the site is created alongside the existing plan/storage.
param location = 'westus2'
param appServicePlanName = 'asp-PoPunkouterSoftware-f1'
param storageAccountName = 'stpopunkoutersoftware2'
param appServicePlanSku = 'F1'
