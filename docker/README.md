# HR Project container images

This folder builds three Linux container images from the repository root:

- `hr-project-backend`: ASP.NET Core API on port `8080`
- `hr-project-frontend`: Blazor WebAssembly hosted by Nginx on port `8080`
- `hr-project-worker`: Attendance background worker

## Build and push to GHCR

Set the non-secret image information in PowerShell:

```powershell
$env:GHCR_USERNAME = "your-github-login"
$env:GHCR_OWNER = "your-user-or-organization"
$env:GHCR_PROJECT = "hr-project"
```

Set `GHCR_PAT` securely in the same PowerShell session, then run:

```powershell
.\docker\build-push-ghcr.bat 1.0.0
```

The script builds and pushes:

```text
ghcr.io/<owner>/hr-project-backend:1.0.0
ghcr.io/<owner>/hr-project-frontend:1.0.0
ghcr.io/<owner>/hr-project-worker:1.0.0
```

After the first push, verify each package is `Private` in GitHub Packages.

## Frontend runtime settings

The frontend image generates `/usr/share/nginx/html/appsettings.json` each time
the container starts. Set these environment variables in the workload:

- `AZURE_AD_AUTHORITY`
- `AZURE_AD_CLIENT_ID`
- `API_BASE_URL`
- `API_SCOPE`

`API_BASE_URL` must be the public browser-accessible API URL, not the internal
Kubernetes Service name.

## Runtime secrets

Do not put connection strings, passwords, client secrets, or GHCR tokens in the
Dockerfiles or images. Supply API and worker configuration at runtime using
Kubernetes Secrets or another secret store. .NET nested configuration keys use
double underscores, for example `ConnectionStrings__HrDatabase`.

