using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
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

var builder = WebApplication.CreateBuilder(args);

if (builder.Environment.IsDevelopment())
{
    // Configure Kestrel Web Server for client certificates.
    builder.Services.Configure<KestrelServerOptions>(serverOptions =>
    {
        serverOptions.ConfigureHttpsDefaults(options =>
        {
            // RequireCertificate works fine.
            options.ClientCertificateMode = ClientCertificateMode.DelayCertificate;
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

/*builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme) // JwtBearer is the "default" auth scheme.
    .AddJwtBearer()
    .AddCertificate(options =>
    {   // `options.AllowedCertificateTypes = CertificateTypes.Chained` by default.
        options.ChainTrustValidationMode = X509ChainTrustMode.CustomRootTrust;
        options.CustomTrustStore.Add(X509Certificate2.CreateFromPem(
            File.ReadAllText("cert/ca.crt").AsSpan()));
        // `/var/ssl/certs/ca.der` in filesystem on Azure?
        options.RevocationMode = X509RevocationMode.NoCheck;
    });*/

/*builder.Services.AddAuthorization(options =>
{
    options.DefaultPolicy = new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme,
            CertificateAuthenticationDefaults.AuthenticationScheme)
        // Check that both authentication schemes provided an authenticated identity (could look for a claim from each).
        .RequireAssertion(
            context => context.User.Identities.Count() == options.DefaultPolicy.AuthenticationSchemes.Count &&
                       context.User.Identities.All(identity => identity.IsAuthenticated))
        .Build();
});*/

var app = builder.Build();

app.Use(next => context =>
{
    context.Request.EnableBuffering(); // so we can model-bind AND access the HttpRequest raw body
    return next(context);
});

//app.UseAuthentication();
//app.UseAuthorization();

// Simple JSON endpoints.
app.MapGet("/who-am-i", (ClaimsPrincipal user) =>
{
    app.Logger.LogInformation("WhoAmI request received, responding 200 OK: {}",
        String.Join(", ", user.Identities.Select(identity => identity.Name)));
    return new
    {
        User = user
    };
});//.RequireAuthorization();

// Keep track of data received.
int devDrcId = 11, devConcorCount = 0, devFdcCount = 0; // increment on valid stores
var devIdToStatus = new ConcurrentDictionary<int, int>();
var devPostedRequests = new List<PostedRequest>();

devIdToStatus.TryAdd(13, 400);

app.MapGet("/data", () =>
{
    if (devPostedRequests.Count > 350)
    {
        devPostedRequests.RemoveRange(0, devPostedRequests.Count-350);
    }
    return Results.Ok(new
    {
        concorCount = devConcorCount,
        fdcCount = devFdcCount,
        postedRequests = devPostedRequests,
    });
});//.RequireAuthorization();

async Task<IResult> HandlePostedRequest(HttpRequest request, string entity, int id, int currentState)
{
    request.Body.Position = 0;
    var rawRequestBody = await new StreamReader(request.Body).ReadToEndAsync();
    app.Logger.LogInformation("Contribution request received: {}", rawRequestBody);

    switch (currentState)
    {
        case 200:
            devPostedRequests.Add(new PostedRequest(DateTime.Now, request.Path, id,  rawRequestBody, currentState, "OK with drcId", entity));
            bool inc = devIdToStatus.TryUpdate(id, 409, currentState);
            if ("Fdc".Equals(entity))
            {
                if (inc) Interlocked.Increment(ref devFdcCount);
                return Results.Ok(new { meta = new {
                        drcId = Interlocked.Increment(ref devDrcId),
                        fdcId = id } });
            }
            else
            {
                if (inc) Interlocked.Increment(ref devConcorCount);
                return Results.Ok(new { meta = new {
                        drcId = Interlocked.Increment(ref devDrcId),
                        concorContributionId = id } });
            }
        case 635:
            devPostedRequests.Add(new PostedRequest(DateTime.Now, request.Path, id, rawRequestBody, 200, "OK without drcId", entity));
            devIdToStatus.TryUpdate(id, 409, currentState);
            return Results.Ok();
        case 409:
            devPostedRequests.Add(new PostedRequest(DateTime.Now, request.Path, id, rawRequestBody, currentState, "Conflict with duplicate-id", ""));
            return Results.Problem(
                $"The {entity} [{id}] already exists in the database",
                request.Path,
                currentState,
                "Conflict",
                "https://laa-debt-collection.service.justice.gov.uk/problem-types#duplicate-id");
        case 659:
            devPostedRequests.Add(new PostedRequest(DateTime.Now, request.Path, id, rawRequestBody, 409, "Conflict without duplicate-id", entity));
            return Results.Conflict();
        case 400:
            devPostedRequests.Add(new PostedRequest(DateTime.Now, request.Path, id, rawRequestBody, currentState, "Bad Request with problemDetail", ""));
            return Results.Problem(
                $"Validation failed in some unspecified way",
                request.Path,
                currentState);
        default:
            devPostedRequests.Add(new PostedRequest(DateTime.Now, request.Path, id, rawRequestBody, 500, "Internal Server Error with problemDetails", ""));
            return Results.Problem(
                $"statusCode {currentState}",
                request.Path,
                500);
    }
}

app.MapPost("/laa/v1/contribution", async (HttpRequest request, [FromBody] ConcorContributionReqRoot body) =>
{
    // Do we have a "preplanned" response? If not, respond 200 this time, and add 409 as next response:
    if (devIdToStatus.TryAdd(body.Data.ConcorContributionId, 200))
    {
        app.Logger.LogInformation("Contribution payload received {}; no predetermined state", body);
        return await HandlePostedRequest(request, "Contribution", body.Data.ConcorContributionId, 200);
    }
    else
    {
        int currentState = devIdToStatus[body.Data.ConcorContributionId];
        app.Logger.LogInformation("ConcorContribution payload received {}; state {}", body, currentState);
        return await HandlePostedRequest(request, "Contribution", body.Data.ConcorContributionId, currentState);
    }
});//.RequireAuthorization();

app.MapPost("/laa/v1/fdc", async (HttpRequest request, [FromBody] FdcReqRoot body) =>
{
    // Do we have a "preplanned" response? If not, respond 200 this time, and add 409 as next response:
    if (devIdToStatus.TryAdd(body.Data.FdcId, 200))
    {
        app.Logger.LogInformation("Fdc payload received {}; no predetermined state", body);
        return await HandlePostedRequest(request, "Fdc", body.Data.FdcId, 200);
    }
    else
    {
        int currentState = devIdToStatus[body.Data.FdcId];
        app.Logger.LogInformation("Fdc payload received {}; state {}", body, currentState);
        return await HandlePostedRequest(request, "Fdc", body.Data.FdcId, currentState);
    }
});//.RequireAuthorization();

app.Run();

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
[UsedImplicitly] record ConcorContributionReqObj(int MaatId, String Flag);
[UsedImplicitly] record ConcorContributionReqData(int ConcorContributionId, ConcorContributionReqObj ConcorContributionObj);
[UsedImplicitly] record ConcorContributionReqRoot(ConcorContributionReqData Data, JsonElement Meta);

[UsedImplicitly] record FdcReqObj(long MaatId);
[UsedImplicitly] record FdcReqData(int FdcId, FdcReqObj FdcObj);
[UsedImplicitly] record FdcReqRoot(FdcReqData Data, JsonElement Meta);
