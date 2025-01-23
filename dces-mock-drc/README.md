# `dces-mock-drc API`

## Azure configuration

1. Installed `dotnet` (initially version 9.0.x, then also version 8.0.x - because there are some issues in 9.0.x).
   Downloaded from MS web page. Unnecessary to actually use the command-line, just to have it available for Rider.
2. Installed `azure-cli` using Homebrew. Used `az login` at command-line to login using web browser (OIDC or similar).
3. Created a resource group for app-specific resources `rg-laadces-mock-drc`. The app service and key vault below are
   assigned to this resource group and given the standard tags.
4. Created an app service called `laadces-mock-drc` (implicitly created an app service plan, or could have been the
   resource group? Anyway, the ASP is Linux, Basic (B1))
5. In the app service's **Settings** / **Configuration** / **General settings** tab, I set
   - **Minimum Inbound TLS Version** to _1.3_
   - **Minimum Inbound TLS Cipher** to _TLS_RSA_WITH_AES_128_GCM_SHA256_
   - **Client certificate mode** to _Allow_
6. In the app service's **Settings** / **Identity** / **System assigned** tab, I set
   - **Status** to _On_, and made a note of the **Object (principal) ID**.
7. Created a key vault with name `kv-laadces-mock-drc`. Added myself and Aminur with role 'Key Vault Administrator'.
8. In the key vault's **Access Control (IAM)** / **Role assignments** tab, I added a role assignment
   - the app service's managed identity was given the _Key Vault Secrets User_ role (hopefully this will allow the app
     service to consume secrets without explicit credentials)
