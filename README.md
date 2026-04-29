# api.intitech.dev

Backend API powering [intitech.dev](https://intitech.dev). Aggregates live data from GitHub and other sources.

Built with **ASP.NET Core 9 Minimal API**, deployed to **Azure App Service** via **GitHub Actions**.

---

## Endpoints

| Method | Path | Description |
|--------|------|-------------|
| GET | `/health` | Health check |
| GET | `/github/summary` | Full GitHub summary — profile, top repos, activity, languages |

### Sample response — `/github/summary`

```json
{
  "profile": {
    "username": "intitech",
    "displayName": "Abdulawwal Intisor",
    "bio": "...",
    "avatarUrl": "...",
    "publicRepos": 24,
    "followers": 12,
    "following": 8,
    "profileUrl": "https://github.com/intitech"
  },
  "topRepos": [
    {
      "name": "viidii",
      "description": "WebRTC video conferencing",
      "url": "https://github.com/intitech/viidii",
      "language": "C#",
      "stars": 3,
      "forks": 0,
      "isForked": false,
      "updatedAt": "2025-04-20T...",
      "topics": ["webrtc", "blazor"]
    }
  ],
  "activity": {
    "totalCommitsThisYear": 147,
    "commitsThisWeek": 12,
    "commitsThisMonth": 43,
    "currentStreak": 5,
    "longestStreak": 5,
    "recentContributions": [
      { "date": "2026-04-28", "count": 3 }
    ]
  },
  "languages": {
    "percentages": { "C#": 72.4, "HTML": 14.2, "CSS": 8.1 },
    "byteCounts": { "C#": 148392, "HTML": 29103 }
  },
  "cachedAt": "2026-04-28T10:00:00Z"
}
```

---

## Local Development

```bash
# Clone
git clone https://github.com/intitech/api.intitech.dev
cd api.intitech.dev/IntitechApi

# Add your GitHub token to user secrets (optional but recommended — higher rate limits)
dotnet user-secrets set "GitHub:Token" "ghp_your_token_here"
dotnet user-secrets set "GitHub:Username" "your-github-username"

# Run
dotnet run
# API available at https://localhost:7xxx or http://localhost:5xxx
```

> Without a token, GitHub API allows 60 requests/hour per IP. With a token: 5,000/hour.

---

## Azure Deployment

### One-time setup

1. Create an **Azure App Service** (Free F1 or B1 tier)
   - Runtime: `.NET 9`
   - OS: Linux
   - Name: `intitech-api` (this becomes `intitech-api.azurewebsites.net`)

2. Set **App Settings** in Azure Portal (Configuration > Application settings):
   ```
   GitHub__Username = your-github-username
   GitHub__Token    = ghp_your_token_here
   ```

3. Download the **Publish Profile** from Azure Portal (Overview > Get publish profile)

4. Add it as a GitHub secret: `AZURE_WEBAPP_PUBLISH_PROFILE`

5. Push to `main` — GitHub Actions handles the rest.

### Custom domain

After deploying, map `api.intitech.dev` to your Azure App Service via:
- Azure Portal > Custom Domains > Add custom domain
- Add a CNAME record in Cloudflare: `api` → `intitech-api.azurewebsites.net`

---

## Cache TTLs

| Source | TTL |
|--------|-----|
| GitHub | 10 minutes |

---

## Roadmap

- [ ] Cloudflare Analytics (zikfash + intitech.dev)
- [ ] Twitter/X profile stats  
- [ ] Chrome Web Store install counts (AI Capture)
- [ ] Substack subscriber count
- [ ] Notion-backed project status
