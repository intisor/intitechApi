using IntitechApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<GitHubService>();
builder.Services.AddSingleton<MetadataService>();
builder.Services.AddHttpClient<WakaTimeService>();
builder.Services.AddSingleton<PortfolioService>();
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

app.MapGet("/", () => Results.Ok(new
{
    message = "Welcome to IntitechApi. Visit /github/summary for GitHub data.",
    timestamp = DateTime.UtcNow
}));

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

app.MapGet("/github/profile", async (GitHubService github) =>
{
    try
    {
        var profile = await github.GetProfileAsync();
        return Results.Ok(profile);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to fetch GitHub profile",
            detail: ex.Message,
            statusCode: 502);
    }
});

app.MapGet("/github/repos", async (GitHubService github) =>
{
    try
    {
        var repos = await github.GetReposAsync();
        return Results.Ok(repos);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to fetch GitHub repositories",
            detail: ex.Message,
            statusCode: 502);
    }
});

app.MapGet("/github/repos/top", async (GitHubService github, int count = 6) =>
{
    try
    {
        var repos = await github.GetTopReposAsync(count);
        return Results.Ok(repos);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to fetch top GitHub repositories",
            detail: ex.Message,
            statusCode: 502);
    }
});

app.MapGet("/github/repo/{repoName}", async (GitHubService github, string repoName) =>
{
    try
    {
        var repo = await github.GetRepoAsync(repoName);
        return repo is not null ? Results.Ok(repo) : Results.NotFound(new { message = "Repository not found." });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to fetch repository details",
            detail: ex.Message,
            statusCode: 502);
    }
});

app.MapGet("/github/activity", async (GitHubService github) =>
{
    try
    {
        var activity = await github.GetActivityAsync();
        return Results.Ok(activity);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to fetch GitHub activity",
            detail: ex.Message,
            statusCode: 502);
    }
});

app.MapGet("/github/languages", async (GitHubService github) =>
{
    try
    {
        var languages = await github.GetLanguageBreakdownAsync();
        return Results.Ok(languages);
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to fetch GitHub language data",
            detail: ex.Message,
            statusCode: 502);
    }
});

// ── Portfolio ─────────────────────────────────────────────────────────────────
app.MapGet("/portfolio/summary", async (PortfolioService portfolio) =>
{
    try
    {
        var summary = await portfolio.GetSummaryAsync();
        return summary is not null ? Results.Ok(summary) : Results.NotFound(new { message = "Portfolio metadata not found." });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Failed to fetch portfolio summary",
            detail: ex.Message,
            statusCode: 502);
    }
});

app.Run();
