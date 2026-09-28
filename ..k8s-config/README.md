# HR Project Kubernetes configuration

Namespace `dev-is` contains three workloads:

- `hrm-api`: `ghcr.io/neotp/hr-project-backend:28.09.2026-v1`
- `hrm-web`: `ghcr.io/neotp/hr-project-frontend:28.09.2026-v1`
- `hrm-attendance-worker`: `ghcr.io/neotp/hr-project-worker:28.09.2026-v1`

URLs are `https://webapp.sisthai.com/Hrmsystem-web/` and
`https://webapp.sisthai.com/Hrmsystem-api/`.

## Cluster

Keep kubeconfig outside Git:

```powershell
$env:KUBECONFIG = "C:\secure\hr-project-kubeconfig.yaml"
.\..k8s-config\kubectl.exe config current-context
.\..k8s-config\kubectl.exe get namespace dev-is
```

## GHCR pull secret

Use a fresh token with `read:packages`; do not save it in YAML:

```powershell
$githubUser = "YOUR_GITHUB_USERNAME"
$githubPat = Read-Host "GitHub PAT" -AsSecureString
$credential = [System.Net.NetworkCredential]::new("", $githubPat).Password
try {
  .\..k8s-config\kubectl.exe -n dev-is create secret docker-registry ghcr-hrm-secret `
    --docker-server=ghcr.io --docker-username=$githubUser --docker-password=$credential `
    --dry-run=client -o yaml | .\..k8s-config\kubectl.exe apply -f -
} finally {
  $credential = $null
}
```

## Application secrets

The API and worker share the `.NET UserSecretsId` named
`HrProject.Api-LocalDevelopment`. Synchronize those existing values directly
to Kubernetes without writing their plaintext values to a YAML file:

```powershell
.\..k8s-config\sync-secrets-from-dotnet.ps1 `
  -KubeConfig "C:\secure\hr-project-kubeconfig.yaml"
```

The script validates every required key before changing the cluster. If Wi-Fi
attendance is not in User Secrets yet, add it once and run the script again:

```powershell
dotnet user-secrets set `
  "ConnectionStrings:WifiAttendanceDatabase" `
  "YOUR_CONNECTION_STRING" `
  --project .\HrProject.AttendanceWorker\HrProject.AttendanceWorker.csproj
```

`hrm-secrets.example.yaml` remains available only as a manual fallback.

## Deploy

```powershell
.\..k8s-config\kubectl.exe apply -k .\..k8s-config
.\..k8s-config\kubectl.exe -n dev-is rollout status deployment/hrm-api
.\..k8s-config\kubectl.exe -n dev-is rollout status deployment/hrm-web
.\..k8s-config\kubectl.exe -n dev-is rollout status deployment/hrm-attendance-worker
```

Inspect without printing secrets:

```powershell
.\..k8s-config\kubectl.exe -n dev-is get pods,svc,ingress
.\..k8s-config\kubectl.exe -n dev-is logs deployment/hrm-api --tail=100
.\..k8s-config\kubectl.exe -n dev-is logs deployment/hrm-attendance-worker --tail=100
```
