# `dces-mock-drc-csaz API`

## Azure configuration

1. Locally installed `dotnet` (initially version 9.0.x, then also version 8.0.x - because there are some issues in
   9.0.x). Downloaded from MS web page. Unnecessary to use from the command-line, but to have it available for Rider.
2. Locally installed `azure-cli` using Homebrew. Used `az login` at command-line, which did login using web browser.
3. Created a resource group for app-specific resources `rg-laadces-mock-drc`. The app service and key vault below are
   assigned to this resource group and given the standard tags.
4. Created an app service called `laadces-mock-drc` (implicitly created an app service plan, or could have been the
   resource group? Anyway, the ASP is Linux, Basic (B1))
5. In the app service's **Settings** / **Configuration** / **General settings** tab, I set:
   - _(optional)_ **Minimum Inbound TLS Version** to _1.3_
   - _(optional)_ **Minimum Inbound TLS Cipher** to _TLS_RSA_WITH_AES_128_GCM_SHA256_
   - _(required for mTLS)_ **Client certificate mode** to _Allow_
6. In the app service's **Settings** / **Identity** / **System assigned** tab, I set:
   - **Status** to _On_, and made a note of the **Object (principal) ID**.
7. Created a key vault with name `kv-laadces-mock-drc`. Added myself and Aminur with role 'Key Vault Administrator'.
8. In the key vault's **Access Control (IAM)** / **Role assignments** tab, I added a role assignment:
   - the app service's managed identity was given the _Key Vault Secrets User_ role (this allows the app service to
     consume secrets without explicit credentials)
9. An App registration was created in Entra ID (by the Azure Ops team)
10. In the App registration page, opened **Manage** / **Manifest** sub page, **Microsoft Graph App Manifest (New)** tab,
    and changed `"requestedAccessTokenVersion"` from `1` to `2`. The `"iss"` attribute in the generated JWT token used
    the hostname `sts.windows.net` instead of `login.microsoftonline.com`.
    [This GitHub issue](https://github.com/AzureAD/microsoft-authentication-library-for-js/issues/560)
    suggested this change, which is also mentioned in
    [this article](https://learn.microsoft.com/en-us/entra/identity-platform/reference-app-manifest#requestedaccesstokenversion-attribute)
    from Microsoft.
11. Note that the previous step should have been done in the Azure LZ Terraform repo: I was contacted later by the
    Azure Ops team and, on my behalf, they merged the GitHub Pull Request
    [HC-1194 add api requested_access_token_version](https://github.com/ministryofjustice/staff-infrastructure-azure-landing-zone-aad/pull/116)
    to add the capability to change the `"requestedAccessTokenVersion"` in their TF module, and to actually change it.
12. Upload some secrets into the Azure Key Vault
    * `az keyvault secret set --name Authentication--AzureAd--ClientId --vault-name kv-laa-dces-mock-drc --value "<clientId>"`
    * `az keyvault secret set --name Authentication--AzureAd--TenantId --vault-name kv-laa-dces-mock-drc --value "<tenantId>"`
    * `az keyvault secret set --name Authentication--Certificate--ClientCa --vault-name kv-laa-dces-mock-drc --file <clientCa.crt>`

