using IntitechApi.Models;
using IntitechApi.Services;

var builder = WebApplication.CreateBuilder(args);

// Services
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<GitHubService>();
builder.Services.AddSingleton<MetadataService>();
builder.Services.AddHttpClient<WakaTimeService>();
builder.Services.AddHttpClient<FreeAiNarrativeService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(25);
});
builder.Services.AddSingleton<PortfolioService>();
builder.Services.AddSingleton<IWorktaleStore, WorktaleStore>();
builder.Services.AddSingleton<IWorktaleQueue, WorktaleQueue>();
builder.Services.AddHostedService<WorktaleNarrativeWorker>();
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

// ── Worktale / Changelog ────────────────────────────────────────────────────
app.MapPost("/api/worktale/ingest", async (
    HttpContext context,
    WorktaleIngestRequest request,
    IWorktaleStore store,
    IWorktaleQueue queue,
    IConfiguration config,
    CancellationToken cancellationToken) =>
{
    if (!IsApiKeyAuthorized(context.Request, config))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Hash) ||
        string.IsNullOrWhiteSpace(request.Message) ||
        string.IsNullOrWhiteSpace(request.Repo))
    {
        return Results.BadRequest(new { message = "hash, message, and repo are required." });
    }

    var entry = await store.AddCommitAsync(request, cancellationToken);
    await queue.EnqueueAsync(entry.Id, cancellationToken);

    return Results.Accepted($"/api/changelog/{entry.Id}", new
    {
        entry.Id,
        status = entry.Status,
        message = "Commit ingested and queued for narrative generation."
    });
});

app.MapGet("/api/changelog", async (IWorktaleStore store, CancellationToken cancellationToken) =>
{
    var entries = await store.GetAllAsync(cancellationToken);
    return Results.Ok(entries);
});

app.MapGet("/api/changelog/{id}", async (string id, IWorktaleStore store, CancellationToken cancellationToken) =>
{
    var entry = await store.GetByIdAsync(id, cancellationToken);
    return entry is null ? Results.NotFound(new { message = "Changelog entry not found." }) : Results.Ok(entry);
});

app.MapPost("/api/changelog/milestone", async (
    HttpContext context,
    ChangelogMilestoneRequest request,
    IWorktaleStore store,
    IConfiguration config,
    CancellationToken cancellationToken) =>
{
    if (!IsApiKeyAuthorized(context.Request, config))
    {
        return Results.Unauthorized();
    }

    if (string.IsNullOrWhiteSpace(request.Title) || string.IsNullOrWhiteSpace(request.Description))
    {
        return Results.BadRequest(new { message = "title and description are required." });
    }

    var entry = await store.AddMilestoneAsync(request, cancellationToken);
    return Results.Ok(entry);
});

app.MapGet("/api/worktale/ai/health", async (
    HttpContext context,
    bool simulatePrimaryFailure,
    FreeAiNarrativeService ai,
    IConfiguration config,
    CancellationToken cancellationToken) =>
{
    if (!IsApiKeyAuthorized(context.Request, config))
    {
        return Results.Unauthorized();
    }

    var result = await ai.ProbeAsync(simulatePrimaryFailure, cancellationToken);
    return result.Success ? Results.Ok(result) : Results.Problem(
        title: "AI health probe failed",
        detail: result.Error,
        statusCode: 503);
});

app.Run();

static bool IsApiKeyAuthorized(HttpRequest request, IConfiguration config)
{
    var configuredKey = config["Worktale:IngestApiKey"];
    if (string.IsNullOrWhiteSpace(configuredKey))
    {
        return false;
    }

    if (!request.Headers.TryGetValue("X-Api-Key", out var providedKey) ||
        string.IsNullOrWhiteSpace(providedKey))
    {
        return false;
    }

    return string.Equals(configuredKey, providedKey.ToString(), StringComparison.Ordinal);
}
