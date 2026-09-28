[CmdletBinding()]
param(
    [string]$Namespace = "dev-is",
    [string]$KubeConfig
)

$ErrorActionPreference = "Stop"

if ($KubeConfig) {
    $env:KUBECONFIG = (Resolve-Path -LiteralPath $KubeConfig).Path
}

$projectRoot = Split-Path -Parent $PSScriptRoot
$apiProject = Join-Path $projectRoot "HrProject.Api\HrProject.Api.csproj"
$kubectl = Join-Path $PSScriptRoot "kubectl.exe"

if (-not (Test-Path -LiteralPath $kubectl)) {
    throw "kubectl.exe was not found at $kubectl"
}

$raw = & dotnet user-secrets list --json --project $apiProject
if ($LASTEXITCODE -ne 0) {
    throw "Unable to read .NET User Secrets."
}

$json = ($raw | Where-Object { $_ -notmatch '^//(BEGIN|END)$' }) -join [Environment]::NewLine
$secretObject = $json | ConvertFrom-Json
$secrets = @{}
$secretObject.PSObject.Properties | ForEach-Object {
    $secrets[$_.Name] = $_.Value
}

$required = @(
    "ConnectionStrings:HrDatabase",
    "ConnectionStrings:HikvisionDatabase",
    "ConnectionStrings:WifiAttendanceDatabase",
    "AzureAd:ClientSecret",
    "LocalJwt:SigningKey",
    "LotusNotes:Endpoint",
    "LotusNotes:Username",
    "LotusNotes:Password",
    "LotusNotes:Database"
)
$missing = @($required | Where-Object {
    -not $secrets.ContainsKey($_) -or [string]::IsNullOrWhiteSpace([string]$secrets[$_])
})
if ($missing.Count -gt 0) {
    throw "Missing .NET User Secret key(s): $($missing -join ', ')"
}

function Apply-OpaqueSecret {
    param(
        [Parameter(Mandatory)] [string]$Name,
        [Parameter(Mandatory)] [hashtable]$StringData
    )

    $manifest = [ordered]@{
        apiVersion = "v1"
        kind = "Secret"
        metadata = [ordered]@{ name = $Name; namespace = $Namespace }
        type = "Opaque"
        stringData = $StringData
    } | ConvertTo-Json -Depth 6 -Compress

    $manifest | & $kubectl apply -f -
    if ($LASTEXITCODE -ne 0) {
        throw "kubectl failed while applying secret $Name."
    }
}

Apply-OpaqueSecret -Name "hrm-api-secrets" -StringData @{
    ConnectionStrings__HrDatabase = $secrets["ConnectionStrings:HrDatabase"]
    AzureAd__ClientSecret = $secrets["AzureAd:ClientSecret"]
    LocalJwt__SigningKey = $secrets["LocalJwt:SigningKey"]
    LOTUS_NOTES_ENDPOINT = $secrets["LotusNotes:Endpoint"]
    LOTUS_NOTES_USERNAME = $secrets["LotusNotes:Username"]
    LOTUS_NOTES_PASSWORD = $secrets["LotusNotes:Password"]
    LOTUS_NOTES_DATABASE = $secrets["LotusNotes:Database"]
}

Apply-OpaqueSecret -Name "hrm-worker-secrets" -StringData @{
    ConnectionStrings__HrDatabase = $secrets["ConnectionStrings:HrDatabase"]
    ConnectionStrings__HikvisionDatabase = $secrets["ConnectionStrings:HikvisionDatabase"]
    ConnectionStrings__WifiAttendanceDatabase = $secrets["ConnectionStrings:WifiAttendanceDatabase"]
}

Write-Host "Kubernetes application secrets were synchronized from .NET User Secrets."
