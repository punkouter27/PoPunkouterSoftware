// Infrastructure for PoPunkouterSoftware.
//
// Faithful, idempotent description of the resources that already exist in the
// 'PoPunkouterSoftware' resource group (subscription 14d963a2-...-553bf01f8de2,
// region West US 2). Deployed in Incremental mode from deploy.yml, so a run
// against the live resource group is a no-op when nothing has drifted.
//
// NOTE: NET_RULES §5 mandates "PoShared (or Po{SolutionName})". The RG
// 'PoPunkouterSoftware' uses the per-solution form — the rule explicitly
// permits both. The Agent Service Plan stays app-local (not in PoShared) on
// purpose: F1 isolation keeps this app's cold starts and quota pressure
// away from any shared plan. The shared Key Vault kv-poshared2 does live in
// PoShared and is referenced by name below.
//
// IMPORTANT: appSettings are intentionally NOT declared on the site resource.
// The running app sources its settings (Key Vault references, the Application
// Insights connection string, etc.) from the portal / runtime. In Incremental
// mode, omitting the appSettings collection leaves those existing settings
// untouched — declaring it here would replace the whole set and wipe them.
targetScope = 'resourceGroup'

@description('App Service name. Must match the live site — defaults to the production app.')
param appName string = 'app-popunkoutersoftware2'

@description('App Service Plan (Windows) hosting the site.')
param appServicePlanName string = 'asp-PoPunkouterSoftware-f1'

@description('Storage account used by the app (Azure Table Storage).')
param storageAccountName string = 'stpopunkoutersoftware2'

@description('Azure region. Defaults to the resource group location.')
param location string = resourceGroup().location

@description('App Service Plan SKU. F1 = Free tier (no Always-On, cold starts).')
param appServicePlanSku string = 'F1'

@description('Shared Key Vault the app reads secrets from (system-assigned MI).')
param sharedKeyVaultName string = 'kv-poshared2'

@description('Resource group that holds the shared Key Vault.')
param sharedKeyVaultResourceGroup string = 'PoShared'

@description('Shared Azure AI Services account backing the /azure status narrative.')
param sharedAiServicesAccountName string = 'po-aiservices-shared2'

@description('App Insights component the site is linked to, surfaced as the portal hidden-link tag.')
param appInsightsResourceId string = '/subscriptions/14d963a2-badc-4a1f-9900-553bf01f8de2/resourceGroups/PoShared/providers/microsoft.insights/components/poappideinsights8f9c9a4e'

// Windows App Service Plan, Free (F1) tier.
resource appServicePlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: appServicePlanName
  location: location
  kind: 'app'
  sku: {
    name: appServicePlanSku
    tier: appServicePlanSku == 'F1' ? 'Free' : 'Basic'
  }
  properties: {
    reserved: false // false = Windows
  }
}

// The site. Windows, .NET 10, HTTPS-only, TLS 1.2, system-assigned identity
// (used to read Key Vault 'kv-poshared2'). Always-On stays off — F1 cannot run it.
resource site 'Microsoft.Web/sites@2024-04-01' = {
  name: appName
  location: location
  kind: 'app'
  identity: {
    type: 'SystemAssigned'
  }
  // Declared because it EXISTS on the live site, and a template that omits it deletes it.
  // `az deployment group what-if` on 2026-09-05 reported `tags -> None` on this resource:
  // the portal writes this hidden-link when App Insights is attached to a web app, and it
  // is what makes the Application Insights blade resolve from the app's own menu. Losing it
  // does not stop telemetry — the connection string does that work — so a full deployment
  // would have quietly broken a portal navigation nobody would connect to a bicep run weeks
  // later. This file's header calls itself a faithful description of what already exists;
  // this is part of being faithful.
  tags: {
    'hidden-link: /app-insights-resource-id': appInsightsResourceId
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      netFrameworkVersion: 'v10.0'
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      http20Enabled: true
      alwaysOn: false
      metadata: [
        {
          name: 'CURRENT_STACK'
          value: 'dotnet'
        }
      ]
    }
  }
}

// Azure Table Storage backing store. HTTPS-only, TLS 1.2, no public blob access.
resource storage 'Microsoft.Storage/storageAccounts@2024-01-01' = {
  name: storageAccountName
  location: location
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
  }
}

// 30-day blob lifecycle: app screenshots are re-captured daily, so any blob untouched
// for 30 days belongs to an app no longer in the portfolio. Table data is aged out in
// code instead (AzureReportStore retention sweep) — lifecycle policies cover blobs only.
resource storageLifecycle 'Microsoft.Storage/storageAccounts/managementPolicies@2024-01-01' = {
  parent: storage
  name: 'default'
  properties: {
    policy: {
      rules: [
        {
          name: 'delete-stale-blobs-30d'
          enabled: true
          type: 'Lifecycle'
          definition: {
            filters: {
              blobTypes: ['blockBlob']
            }
            actions: {
              baseBlob: {
                delete: {
                  daysAfterModificationGreaterThan: 30
                }
              }
            }
          }
        }
      ]
    }
  }
}

// Least-privilege binding: grant the site's system-assigned identity read-only
// access to secrets in the shared Key Vault — nothing more. Scoped to the shared
// RG because that is where kv-poshared2 lives.
module keyVaultAccess 'modules/keyvault-secrets-user.bicep' = {
  name: 'kv-secrets-user-${appName}'
  scope: resourceGroup(sharedKeyVaultResourceGroup)
  params: {
    keyVaultName: sharedKeyVaultName
    principalId: site.identity.principalId
  }
}

// Inference-only access to the shared AI Services account, so the /azure status
// narrative (AiTriageService) can authenticate with the site's managed identity
// rather than an API key. Without this the app still works — it falls back to the
// 'PoPunkouterSoftware--AzureOpenAI--ApiKey' vault secret — but the keyless path
// is the one worth having, and it is the only path that needs no secret at all.
module aiServicesAccess 'modules/cognitive-services-openai-user.bicep' = {
  name: 'ai-openai-user-${appName}'
  scope: resourceGroup(sharedKeyVaultResourceGroup)
  params: {
    aiServicesAccountName: sharedAiServicesAccountName
    principalId: site.identity.principalId
  }
}

// The whole point of the app: read-only access to the subscription it reports on.
// Deployed at subscription scope from this resource-group deployment — see the module
// header for what breaks without it (everything, silently).
module inventoryReader 'modules/subscription-inventory-reader.bicep' = {
  name: 'sub-inventory-reader-${appName}'
  scope: subscription()
  params: {
    principalId: site.identity.principalId
  }
}

// Data-plane access to this app's own storage account. The control-plane Reader grant
// above does NOT cover reading or writing table rows and blobs: Azure storage data
// operations are governed by their own roles, and the app authenticates to them with
// the same managed identity (AzureTableStorage:Endpoint / AzureBlobStorage:Endpoint set,
// no connection string). Table = report history and uptime samples; Blob = the gzipped
// report bodies and the app-screenshots container.
var storageTableDataContributorRoleId = '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
var storageBlobDataContributorRoleId = 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'

resource tableDataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, site.id, storageTableDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageTableDataContributorRoleId)
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

resource blobDataAccess 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, site.id, storageBlobDataContributorRoleId)
  scope: storage
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', storageBlobDataContributorRoleId)
    principalId: site.identity.principalId
    principalType: 'ServicePrincipal'
  }
}

output appName string = site.name
output appServicePlanId string = appServicePlan.id
output sitePrincipalId string = site.identity.principalId
