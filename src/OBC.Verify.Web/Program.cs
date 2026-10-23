using OBC.Verify.Web.Components;
using OBC.Verify.Web.Services;
using OBC.Verify.Web.Services.Fetching;
using OBC.Verify.Web.Services.Solana;
using OBC.Verify.Web.Services.Standard;

var builder = WebApplication.CreateBuilder(args);

// Off-chain documents are downloaded from URLs found on-chain, so only through the SSRF-safe fetcher.
builder.Services.AddSingleton(new SafeFetchOptions());
builder.Services.AddHttpClient<IDocumentFetcher, SafeHttpFetcher>()
    .ConfigurePrimaryHttpMessageHandler(SafeHttpFetcher.CreateHandler);
builder.Services.AddSingleton<ISolanaReaderFactory, SolnetSolanaReaderFactory>();
builder.Services.AddSingleton(builder.Configuration.GetSection("Verification:ChainScan").Get<ChainScanOptions>() ?? new ChainScanOptions());
builder.Services.AddScoped<VerificationService>();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
