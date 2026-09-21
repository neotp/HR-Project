[CmdletBinding()]
param(
    [datetime]$FileDate = (Get-Date),
    [string]$SourceDirectory = '\\172.21.130.198\auto_report\SIS\JobApplication',
    [string]$FilePath,
    [string]$Configuration = 'Release',
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDirectory
$projectPath = Join-Path $projectRoot 'HrProject.JobApplicationImporter\HrProject.JobApplicationImporter.csproj'
$importerDll = Join-Path $projectRoot "HrProject.JobApplicationImporter\bin\$Configuration\net8.0\HrProject.JobApplicationImporter.dll"
$logDirectory = Join-Path $projectRoot 'Logs\JobApplicationImport'

if ([string]::IsNullOrWhiteSpace($FilePath)) {
    $fileName = 'JA_{0}.xls' -f $FileDate.ToString('dd_MM_yyyy')
    $FilePath = Join-Path $SourceDirectory $fileName
}

if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) {
    Write-Error "ไม่พบไฟล์ Job Application: $FilePath"
    exit 2
}

New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
$logPath = Join-Path $logDirectory ('Import_{0}.log' -f (Get-Date -Format 'yyyyMMdd_HHmmss'))

try {
    if (-not (Test-Path -LiteralPath $importerDll -PathType Leaf)) {
        & dotnet build $projectPath --configuration $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "Build ตัวนำเข้าไม่สำเร็จ (Exit Code $LASTEXITCODE)"
        }
    }
    "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] START $FilePath" | Tee-Object -FilePath $logPath
    $importArguments = @($importerDll, '--file', $FilePath)
    if ($DryRun) {
        $importArguments += '--dry-run'
    }
    & dotnet @importArguments 2>&1 |
        Tee-Object -FilePath $logPath -Append
    $importExitCode = $LASTEXITCODE
    "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] END exitCode=$importExitCode" | Tee-Object -FilePath $logPath -Append
    exit $importExitCode
}
catch {
    "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] FAILED $($_.Exception.Message)" | Tee-Object -FilePath $logPath -Append
    exit 1
}
