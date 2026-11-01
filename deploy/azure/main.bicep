// Azure infrastructure for Helio.NET: Container Apps + Azure OpenAI + Azure AI Search + Azure SQL + Redis + Log Analytics.
// Deploy: az deployment group create -g <rg> -f deploy/azure/main.bicep -p environmentName=prod image=ghcr.io/...@sha256:...
// Identity: the container app uses a system-assigned managed identity for the model, the search index AND the
// database (Entra-only SQL authentication; no SQL password exists). The only secrets are the API keys and the
// audit pepper, held as Container Apps secrets; move them to Key Vault references when a vault is in place.
//
// Post-deploy, once, as the SQL Entra admin (the schema itself is created by the app on first start):
//   CREATE USER [helio-<env>-api] FROM EXTERNAL PROVIDER;
//   ALTER ROLE db_ddladmin ADD MEMBER [helio-<env>-api];
//   ALTER ROLE db_datareader ADD MEMBER [helio-<env>-api];
//   ALTER ROLE db_datawriter ADD MEMBER [helio-<env>-api];

targetScope = 'resourceGroup'

@description('Short environment name used in resource names.')
param environmentName string = 'dev'

@description('Container image to run, pinned by digest (ghcr.io/<owner>/helio-dotnet@sha256:...).')
param image string

@description('Azure region.')
param location string = resourceGroup().location

@description('Object id of the Entra group that administers the SQL server.')
param sqlAdminGroupObjectId string

@description('Display name of that group.')
param sqlAdminGroupName string = 'helio-sql-admins'

@secure()
param operatorApiKey string

@secure()
param readerApiKey string

@secure()
@description('HMAC key for pseudonymising caller identities in audit records.')
param callerHashPepper string

var prefix = 'helio-${environmentName}'

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: '${prefix}-logs'
  location: location
  properties: { sku: { name: 'PerGB2018' }, retentionInDays: 30 }
}

resource insights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${prefix}-insights'
  location: location
  kind: 'web'
  properties: { Application_Type: 'web', WorkspaceResourceId: logs.id }
}

resource openai 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: '${prefix}-openai'
  location: location
  kind: 'OpenAI'
  sku: { name: 'S0' }
  properties: { customSubDomainName: '${prefix}-openai', publicNetworkAccess: 'Enabled', disableLocalAuth: true }
}

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openai
  name: 'gpt-4o-mini'
  sku: { name: 'GlobalStandard', capacity: 30 }
  properties: { model: { format: 'OpenAI', name: 'gpt-4o-mini', version: '2024-07-18' } }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openai
  name: 'text-embedding-3-small'
  sku: { name: 'Standard', capacity: 120 }
  properties: { model: { format: 'OpenAI', name: 'text-embedding-3-small', version: '1' } }
  dependsOn: [chatDeployment]
}

resource search 'Microsoft.Search/searchServices@2024-06-01-preview' = {
  name: '${prefix}-search'
  location: location
  sku: { name: 'basic' }
  // Entra-only: admin and query keys are disabled, so a leaked key is not a thing that can exist.
  properties: { replicaCount: 1, partitionCount: 1, hostingMode: 'default', semanticSearch: 'standard', disableLocalAuth: true }
}

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${prefix}-sql'
  location: location
  properties: {
    minimalTlsVersion: '1.2'
    administrators: {
      administratorType: 'ActiveDirectory'
      azureADOnlyAuthentication: true
      principalType: 'Group'
      login: sqlAdminGroupName
      sid: sqlAdminGroupObjectId
      tenantId: tenant().tenantId
    }
  }
}

resource sqlDb 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'helio'
  location: location
  sku: { name: 'GP_S_Gen5', tier: 'GeneralPurpose', family: 'Gen5', capacity: 1 }
  properties: { autoPauseDelay: 60, minCapacity: json('0.5') }
}

// No "allow Azure services" (0.0.0.0) rule: only the Container Apps environment's egress address may reach SQL.
// For a VNet-integrated environment replace this with a private endpoint and publicNetworkAccess: 'Disabled'.
resource sqlFirewall 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'container-apps-egress'
  properties: { startIpAddress: env.properties.staticIp, endIpAddress: env.properties.staticIp }
}

resource redis 'Microsoft.Cache/redis@2024-03-01' = {
  name: '${prefix}-redis'
  location: location
  properties: { sku: { name: 'Basic', family: 'C', capacity: 0 }, minimumTlsVersion: '1.2', enableNonSslPort: false }
}

resource env 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: '${prefix}-env'
  location: location
  properties: {
    appLogsConfiguration: { destination: 'log-analytics', logAnalyticsConfiguration: { customerId: logs.properties.customerId, sharedKey: logs.listKeys().primarySharedKey } }
  }
}

resource app 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${prefix}-api'
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    managedEnvironmentId: env.id
    configuration: {
      ingress: {
        external: true
        targetPort: 8080
        transport: 'http'
        allowInsecure: false
      }
      secrets: [
        { name: 'redis-connection', value: '${redis.properties.hostName}:6380,password=${redis.listKeys().primaryKey},ssl=True,abortConnect=False' }
        { name: 'operator-key', value: operatorApiKey }
        { name: 'reader-key', value: readerApiKey }
        { name: 'caller-hash-pepper', value: callerHashPepper }
        { name: 'appinsights', value: insights.properties.ConnectionString }
      ]
    }
    template: {
      containers: [
        {
          name: 'api'
          image: image
          resources: { cpu: json('0.5'), memory: '1Gi' }
          env: [
            { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
            { name: 'Proxy__TrustedNetworks__0', value: '100.100.0.0/16' } // Container Apps envoy ingress range; adjust per environment
            { name: 'Ai__Provider', value: 'azure-openai' }
            { name: 'Ai__Endpoint', value: openai.properties.endpoint }
            { name: 'Storage__Database', value: 'sqlserver' }
            { name: 'Storage__VectorStore', value: 'azure-ai-search' }
            { name: 'Storage__SearchEndpoint', value: 'https://${search.name}.search.windows.net' }
            // Managed identity for SQL as well: no password anywhere in this template.
            { name: 'Storage__ConnectionString', value: 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=helio;Authentication=Active Directory Default;Encrypt=True;' }
            { name: 'Storage__RedisConnectionString', secretRef: 'redis-connection' }
            { name: 'Security__OperatorApiKeys__0', secretRef: 'operator-key' }
            { name: 'Security__ReaderApiKeys__0', secretRef: 'reader-key' }
            { name: 'Security__CallerHashPepper', secretRef: 'caller-hash-pepper' }
            { name: 'Retention__QueryRecordRetention', value: '90.00:00:00' }
            { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', secretRef: 'appinsights' }
          ]
          probes: [
            { type: 'Startup', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 5, failureThreshold: 24 }
            { type: 'Readiness', httpGet: { path: '/health/ready', port: 8080 }, periodSeconds: 10 }
            { type: 'Liveness', httpGet: { path: '/health/live', port: 8080 }, periodSeconds: 15 }
          ]
        }
      ]
      scale: { minReplicas: 1, maxReplicas: 10, rules: [ { name: 'http', http: { metadata: { concurrentRequests: '40' } } } ] }
    }
  }
}

// Role assignments: the app's managed identity may call the model and read/write the search index. No keys.
resource openaiUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openai.id, app.id, 'openai-user')
  scope: openai
  properties: { principalId: app.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd') }
}

resource searchContributor 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(search.id, app.id, 'search-index-contributor')
  scope: search
  properties: { principalId: app.identity.principalId, principalType: 'ServicePrincipal', roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7') }
}

output apiUrl string = 'https://${app.properties.configuration.ingress.fqdn}'
output openaiEndpoint string = openai.properties.endpoint
