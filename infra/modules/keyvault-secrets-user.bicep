// Grants a principal least-privilege read access (get/list secrets) on an
// existing Key Vault. kv-poshared2 has RBAC authorization DISABLED and uses the
// classic access-policy model, so access must be granted via an access policy —
// an RBAC role assignment would be silently ineffective on this vault.
//
// Deployed at the scope of the vault's resource group (kv-poshared2 lives in the
// shared 'PoShared' RG, not the app's RG), so this is invoked as a module with
// `scope: resourceGroup('PoShared')`.
targetScope = 'resourceGroup'

@description('Name of the existing shared Key Vault, e.g. kv-poshared2.')
param keyVaultName string

@description('Object (principal) ID of the identity to grant access to.')
param principalId string

resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' existing = {
  name: keyVaultName
}

// Add (not replace) a get/list-secrets access policy for the identity. Using the
// 'add' child resource merges this entry, leaving other apps' policies intact.
resource accessPolicy 'Microsoft.KeyVault/vaults/accessPolicies@2024-04-01-preview' = {
  name: 'add'
  parent: keyVault
  properties: {
    accessPolicies: [
      {
        tenantId: keyVault.properties.tenantId
        objectId: principalId
        permissions: {
          secrets: ['get', 'list']
        }
      }
    ]
  }
}
