using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Azure;
using Azure.Identity;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    // Configure Kestrel Web Server for client certificates.
    builder.Services.Configure<KestrelServerOptions>(serverOptions =>
    {
        serverOptions.ConfigureHttpsDefaults(options =>
        {
            // RequireCertificate works fine.
            options.ClientCertificateMode = ClientCertificateMode.RequireCertificate;
            options.AllowAnyClientCertificate(); // Server disconnects client without this.
        });
    });
}
else
{
    var vaultUri = builder.Configuration["KeyVault:VaultUri"];
    if (!string.IsNullOrEmpty(vaultUri))
    {
        builder.Configuration.AddAzureKeyVault(new Uri(vaultUri), new DefaultAzureCredential());
    }
    builder.Logging.AddAzureWebAppDiagnostics();
}

// Configure JSON serialization to allow object reference cycles to be ignored (else ClaimsPrincipal won't serialize).
builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
    options.SerializerOptions.ReferenceHandler = ReferenceHandler.IgnoreCycles);

var clientId = builder.Configuration["Authentication:AzureAd:ClientId"];
var tenantId = builder.Configuration["Authentication:AzureAd:TenantId"];
var clientCa = builder.Configuration["Authentication:Certificate:ClientCa"];

builder.Services.AddCertificateForwarding(options => options.CertificateHeader = "X-ARR-ClientCert");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme) // JwtBearer is the "default" auth scheme.
    .AddJwtBearer(options =>
    {
        options.Audience = clientId;
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
    })
    .AddCertificate(options =>
    {   // `options.AllowedCertificateTypes = CertificateTypes.Chained` by default.
        options.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
        options.CustomTrustStore.Add(X509Certificate2.CreateFromPem(clientCa.AsSpan()));
        options.RevocationMode = X509RevocationMode.NoCheck;
    });

builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(
            JwtBearerDefaults.AuthenticationScheme,
            CertificateAuthenticationDefaults.AuthenticationScheme)
        // Check that both authentication schemes provided an authenticated identity (could look for a claim from each).
        .RequireAssertion(context =>
            context.User.Identities.Count() == options.DefaultPolicy.AuthenticationSchemes.Count
            && context.User.Identities.All(identity => identity.IsAuthenticated))
        .Build();
});

var app = builder.Build();

app.Use(next => context =>
{
    context.Request.EnableBuffering(); // so we can model-bind AND access the HttpRequest raw body
    return next(context);
});

app.UseCertificateForwarding();
app.UseAuthentication();
app.UseAuthorization();

////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

// Keep track of data received.
int dataDrcId = 11, dataConcorCount = 0, dataFdcCount = 0; // increment on valid stores
var dataIdToStatus = new ConcurrentDictionary<int, int>();
var dataPostedRequests = new List<PostedRequest>();

////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

// Simple JSON endpoints.
app.MapGet("/who-am-i", (ClaimsPrincipal user) =>
{
    app.Logger.LogInformation("WhoAmI request received, responding 200 OK: {}",
        String.Join(", ", user.Identities.Select(identity => identity.Name)));
    return new { User = user };
}).RequireAuthorization();

app.MapDelete("/setup", () => dataIdToStatus.Clear()).RequireAuthorization();
app.MapGet("/setup", () => new
{
    data = dataIdToStatus.Select(kvp => new IdToStatus(kvp.Key, kvp.Value)).ToList()
}).RequireAuthorization();
app.MapPost("/setup", ([FromBody] SetupRoot body) =>
{
    foreach (var idToStatus in body.Data)
    {
        dataIdToStatus.AddOrUpdate(idToStatus.Id, idToStatus.StatusCode, (_, _) => idToStatus.StatusCode);
    }
    return Results.Created();
}).RequireAuthorization();

app.MapDelete("/requests", () => dataPostedRequests.Clear()).RequireAuthorization();
app.MapGet("/requests", () => new
{
    data = dataPostedRequests
}).RequireAuthorization();

app.MapPost("/laa/v1/contribution", async (HttpRequest request, [FromBody] ConcorContributionReqRoot body) =>
{
    var statusCode = dataIdToStatus.TryAdd(body.Data.ConcorContributionId, 200)
        ? 200
        : dataIdToStatus[body.Data.ConcorContributionId];
    return await HandlePostedRequest(request, body.Data.ConcorContributionId, statusCode, "Contribution");
}).RequireAuthorization();

app.MapPost("/laa/v1/fdc", async (HttpRequest request, [FromBody] FdcReqRoot body) =>
{
    var statusCode = dataIdToStatus.TryAdd(body.Data.FdcId, 200)
        ? 200
        : dataIdToStatus[body.Data.FdcId];
    return await HandlePostedRequest(request, body.Data.FdcId, statusCode, "Fdc");
}).RequireAuthorization();

app.Run();
return;

////////////////////////////////////////////////////////////////////////////////////////////////////////////////////////

async Task Record(HttpRequest request, int id, int statusCode, string responseType, string storedType)
{
    var requestPath = request.Path.Value;
    request.Body.Position = 0;
    var requestBody = await new StreamReader(request.Body).ReadToEndAsync();
    app.Logger.LogInformation("POST {} : `{}`", requestPath, requestBody);
    dataPostedRequests.Add(new PostedRequest(DateTime.Now, requestPath ?? "", id, requestBody, statusCode, responseType, storedType));
    if (dataPostedRequests.Count > 1000) dataPostedRequests.RemoveRange(0, dataPostedRequests.Count - 1000);
}

async Task<IResult> HandlePostedRequest(HttpRequest request, int id, int statusCode, string storedType)
{
    switch (statusCode)
    {
        case 200:
            await Record(request, id, statusCode, "OK (meta,200)", storedType);
            bool inc = dataIdToStatus.TryUpdate(id, 634, statusCode);
            if ("Fdc".Equals(storedType))
            {
                if (inc) Interlocked.Increment(ref dataFdcCount);
                return Results.Ok(new { meta = new {
                        drcId = Interlocked.Increment(ref dataDrcId),
                        fdcId = id } });
            }
            else
            {
                if (inc) Interlocked.Increment(ref dataConcorCount);
                return Results.Ok(new { meta = new {
                        drcId = Interlocked.Increment(ref dataDrcId),
                        concorContributionId = id } });
            }
        case 400:
            await Record(request, id, statusCode, "Bad Request (ProblemDetail,400)", "");
            return Results.Problem(
                $"Validation failed in some unspecified way",
                request.Path,
                statusCode);
        case 404:
            await Record(request, id, statusCode, "Not Found (ProblemDetail,404)", "");
            return Results.Problem(
                $"Not found in some unspecified way",
                request.Path,
                statusCode);
        case 409:
            await Record(request, id, statusCode, "Conflict (ProblemDetail,409)", "");
            return Results.Problem(
                $"Conflict in some unspecified way",
                request.Path,
                statusCode);
        case 634: // special 409 that is actually a success (has special "type" in response body).
            await Record(request, id, statusCode, "Conflict (duplicate-id,634)", "");
            return Results.Problem(
                $"The {storedType} [{id}] already exists in the database",
                request.Path,
                statusCode,
                "Conflict",
                "https://laa-debt-collection.service.justice.gov.uk/problem-types#duplicate-id");
        case 635: // special 200 that is actually a failure (has empty response body).
            await Record(request, id, 200, "OK (empty,635)", storedType);
            return Results.Ok();
        default:
            await Record(request, id, 500, $"Internal Server Error (ProblemDetail,{statusCode})", "");
            return Results.Problem(
                $"Internal server error in some unspecified way ({statusCode})",
                request.Path,
                500);
    }
}

// We keep a list of the last N requests
[UsedImplicitly] record PostedRequest(
    DateTime DateTime,
    string UriPath,
    int Id,
    string RequestBody,
    int StatusCode,
    string ResponseType,
    string StoredType);

// DTOs for JSON request bodies
[UsedImplicitly] record IdToStatus(int Id, int StatusCode);
[UsedImplicitly] record SetupRoot(List<IdToStatus> Data);
[UsedImplicitly] record ConcorContributionReqObj(int MaatId, String Flag);
[UsedImplicitly] record ConcorContributionReqData(int ConcorContributionId, ConcorContributionReqObj ConcorContributionObj);
[UsedImplicitly] record ConcorContributionReqRoot(ConcorContributionReqData Data, JsonElement Meta);
[UsedImplicitly] record FdcReqObj(long MaatId);
[UsedImplicitly] record FdcReqData(int FdcId, FdcReqObj FdcObj);
[UsedImplicitly] record FdcReqRoot(FdcReqData Data, JsonElement Meta);
