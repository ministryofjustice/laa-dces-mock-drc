using System.Collections.Concurrent;
using System.Security.Claims;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using JetBrains.Annotations;
using Microsoft.AspNetCore.Authentication.Certificate;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

//builder.Logging.AddConsole();
//builder.Logging.AddAzureWebAppDiagnostics();

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
int uniqueIndex = 50_000_000;
var concorContributions = new ConcurrentDictionary<int, ConcorContributionReqRoot>();
var fdcContributions = new ConcurrentDictionary<int, FdcReqRoot>();
var concorReqCountByStatus = new ConcurrentDictionary<String, int>();
var fdcReqCountByStatus = new ConcurrentDictionary<String, int>();

app.MapGet("/laa/v1/stats", () =>
{
    app.Logger.LogInformation("stats request received; responding concorStoredCount:{}, fdcStoredCount:{}",
        concorContributions.Count, fdcContributions.Count);
    return new
    {
        concorStoredCount = concorContributions.Count,
        fdcStoredCount = fdcContributions.Count,
        concorRequestCounts = concorReqCountByStatus,
        fdcRequestCounts = fdcReqCountByStatus
    };
});//.RequireAuthorization();

app.MapPost("/laa/v1/contribution", ([FromBody] ConcorContributionReqRoot body) =>
{
    if (concorContributions.TryAdd(body.Data.ConcorContributionId, body))
    {
        app.Logger.LogInformation("ConcorContribution payload received {}; responding 200 OK", body);
        concorReqCountByStatus.AddOrUpdate("200 OK", 1, (status, count) => count + 1);
        return Results.Ok(new
        {
            drcId = Interlocked.Increment(ref uniqueIndex).ToString(),
            concorContributionId = body.Data.ConcorContributionId
        });
    }
    else
    {
        app.Logger.LogInformation("ConcorContribution payload received {}; responding 409 Conflict", body);
        concorReqCountByStatus.AddOrUpdate("409 Conflict", 1, (status, count) => count + 1);
        return Results.Problem( // use our own spelling mistake (concur instead of condor):
            $"The concurContributionId [{body.Data.ConcorContributionId}] already exists in the database",
            "/api/contribution",
            409,
            "Conflict",
            "https://laa-debt-collection.service.justice.gov.uk/problem-types#duplicate-id");
    }
});//.RequireAuthorization();

app.MapPost("/laa/v1/fdc", ([FromBody] FdcReqRoot body) =>
{
    if (fdcContributions.TryAdd(body.Data.FdcId, body))
    {
        app.Logger.LogInformation("FDC payload received {}; responding 200 OK", body);
        fdcReqCountByStatus.AddOrUpdate("200 OK", 1, (status, count) => count + 1);
        return Results.Ok(new
        {
                drcId = Interlocked.Increment(ref uniqueIndex).ToString(),
                fdcId = body.Data.FdcId
        });
    }
    else
    {
        app.Logger.LogInformation("FDC payload received {}; responding 409 Conflict", body);
        fdcReqCountByStatus.AddOrUpdate("409 Conflict", 1, (status, count) => count + 1);
        return Results.Problem(
            $"The fdcId [{body.Data.FdcId}] already exists in the database",
            "/api/fdc",
            409,
            "Conflict",
            "https://laa-debt-collection.service.justice.gov.uk/problem-types#duplicate-id");
    }
});//.RequireAuthorization();

app.Run();

// DTOs for JSON requests
[UsedImplicitly] record ConcorContributionReqObj(int MaatId, String Flag);
[UsedImplicitly] record ConcorContributionReqData(int ConcorContributionId, ConcorContributionReqObj ConcorContributionObj);
record ConcorContributionReqRoot([UsedImplicitly] ConcorContributionReqData Data, JsonElement Meta);

[UsedImplicitly] record FdcReqObj(long MaatId);
[UsedImplicitly] record FdcReqData(int FdcId, FdcReqObj FdcObj);
record FdcReqRoot([UsedImplicitly] FdcReqData Data, JsonElement Meta);
