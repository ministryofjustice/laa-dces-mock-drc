# `dces-mock-drc-csaz API`

## Azure configuration

1. An Azure subscription, `MoJ-ALZ-Devl-Spoke-LAADigitalDCES` was created by the Azure Ops team in our
   "Ministry of Justice (Development)" directory (note this is a distinct directory and account domain from our
   M365 subscription).
2. Within the "Ministry of Justice (Development)" directory, you will need to be assigned an account by the Azure Ops
   team. Note that these accounts are named like `firstname.lastname.digital@devl.justice.gov.uk` (`devl.justice.gov.uk`
   being the domain for this directory / landing zone). Your account have access to the subscription in step 1.
3. I locally installed `dotnet` (initially version 9.0.x, then also version 8.0.x - because there are some issues in
   9.0.x). Downloaded from MS web page. Unnecessary to use from the command-line, but to have it available for Rider.
4. I locally installed `azure-cli` using Homebrew. Use `az login` at command-line, which did a login to the Azure portal
   using a web browser and the account mentioned in step 2.
5. Created a resource group for app-specific resources `rg-laadces-mock-drc`. The app service and key vault below are
   assigned to this resource group and given the standard tags.
6. Created an app service called `laadces-mock-drc` (which implicitly created an app service plan, which I selected as
   "Linux, Basic (B1)").
7. In the app service's **Settings** / **Configuration** / **General settings** tab, I set **Client certificate mode**
   to _Required_.
8. In the app service's **Settings** / **Identity** / **System assigned** tab, I set **Status** to _On_, and made a note
   of the **Object (principal) ID**.
9. Created a key vault with name `kv-laadces-mock-drc`. Added myself and Aminur with role '_Key Vault Administrator_'.
10. In the key vault's **Access Control (IAM)** / **Role assignments** tab, I added a role assignment for the app
    service's managed identity with the role '_Key Vault Secrets User_' (this allows the app service to consume secrets
    without explicit credentials)
11. An App registration was created in Entra ID (by the Azure Ops team)
12. In the App registration page, opened **Manage** / **Manifest** sub page, **Microsoft Graph App Manifest (New)** tab,
    and changed `"requestedAccessTokenVersion"` from `1` to `2`. The `"iss"` attribute in the generated JWT token used
    the hostname `sts.windows.net` instead of `login.microsoftonline.com`.
    [This GitHub issue](https://github.com/AzureAD/microsoft-authentication-library-for-js/issues/560)
    suggested this change, which is also mentioned in
    [this article](https://learn.microsoft.com/en-us/entra/identity-platform/reference-app-manifest#requestedaccesstokenversion-attribute)
    from Microsoft.
13. Note that the previous step should have been done in the Azure LZ Terraform repo: I was contacted later by the
    Azure Ops team and, on my behalf, they merged the GitHub Pull Request
    [HC-1194 add api requested_access_token_version](https://github.com/ministryofjustice/staff-infrastructure-azure-landing-zone-aad/pull/116)
    to add the capability to change the `"requestedAccessTokenVersion"` in their TF module, and to actually change it.
14. Upload some secrets into the Azure Key Vault
    * `az keyvault secret set --name Authentication--AzureAd--ClientId --vault-name kv-laa-dces-mock-drc --value "<clientId>"`
    * `az keyvault secret set --name Authentication--AzureAd--TenantId --vault-name kv-laa-dces-mock-drc --value "<tenantId>"`
    * `az keyvault secret set --name Authentication--Certificate--ClientCa --vault-name kv-laa-dces-mock-drc --file <clientCa.crt>`
15. When published into the Azure App Service, the application has access to the Azure Key Vault. However, when
    developing locally in JetBrains Rider, right-click on the project, and select **Tools** > **.NET User Secrets**
    from the popup menu, to create the secrets (use ':' instead of '--' in the names) in a local secrets store.
