// Grants a principal inference-only access to an existing Azure AI Services
// (Cognitive Services) account, so the app can call chat completions with its
// managed identity instead of a shared API key.
//
// Unlike kv-poshared2 — which has RBAC authorization DISABLED and therefore needs
// a classic access policy (see keyvault-secrets-user.bicep) — Cognitive Services
// data-plane access is ALWAYS RBAC. A role assignment is the only mechanism here,
// and an access policy would be meaningless.
//
// Deployed at the scope of the AI account's resource group (po-aiservices-shared2
// lives in the shared 'PoShared' RG, not the app's RG), so this is invoked as a
// module with `scope: resourceGroup('PoShared')`.
targetScope = 'resourceGroup'

@description('Name of the existing shared AI Services account, e.g. po-aiservices-shared2.')
param aiServicesAccountName string

@description('Object (principal) ID of the identity to grant inference access to.')
param principalId string

// 'Cognitive Services OpenAI User' — read + inference on deployments, no
// management rights: the identity cannot create, modify or delete deployments,
// and cannot read the account's API keys. This is the least privilege that still
// allows a chat-completions call.
var openAiUserRoleId = '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd'

resource aiServices 'Microsoft.CognitiveServices/accounts@2024-10-01' existing = {
  name: aiServicesAccountName
}

resource openAiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  // Deterministic name: re-deploying is a no-op rather than a duplicate-assignment
  // error, which is what Incremental mode needs.
  name: guid(aiServices.id, principalId, openAiUserRoleId)
  scope: aiServices
  properties: {
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', openAiUserRoleId)
    principalId: principalId
    // Stated explicitly so the assignment does not fail while a freshly created
    // managed identity is still replicating through Entra.
    principalType: 'ServicePrincipal'
  }
}
