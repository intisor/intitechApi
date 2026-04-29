using IntitechApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient<GitHubService>();
builder.Services.AddOpenApi();

// CORS — only allow portfolio origin
builder.Services.AddCors(options =>
{
    options.AddPolicy("PortfolioOnly", policy =>
    {
        policy
            .WithOrigins(
                "https://intitech.dev",
                "https://www.intitech.dev",
                "http://localhost:3000",  // local dev
                "http://localhost:5173"   // vite dev
            )
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

var app = builder.Build();

app.UseCors("PortfolioOnly");

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

// ── Health ────────────────────────────────────────────────────────────────────
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    timestamp = DateTime.UtcNow,
    version = "1.0.0"
}));

// ── GitHub ────────────────────────────────────────────────────────────────────
app.MapGet("/github/summary", async (GitHubService github) =>
{
    try
    {
        var summary = await github.GetSummaryAsync();
        return Results.Ok(summary);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to fetch GitHub data",
            detail: ex.Message,
            statusCode: 502);
    }
});

app.Run();
