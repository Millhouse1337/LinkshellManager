# Deploy LSManager to the droplet.
# Usage:
#   .\deploy\deploy.ps1 -DropletHost root@1.2.3.4
#   .\deploy\deploy.ps1 -DropletHost lsmanager-deploy@1.2.3.4 -SshKey "$HOME\.ssh\id_ed25519"
#
# Requires: dotnet 8 SDK, ssh + scp on PATH (Windows 10+ has these built in),
#           Angular CLI deps already installed in discord-activity\.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DropletHost,

    [string]$SshKey,

    [string]$RemoteAppDir = "/var/www/lsmanager",

    [switch]$SkipFrontend
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$sshArgs = @()
if ($SshKey) { $sshArgs += @('-i', $SshKey) }

function Invoke-Ssh {
    param([string]$Cmd)
    & ssh @sshArgs $DropletHost $Cmd
    if ($LASTEXITCODE -ne 0) { throw "ssh failed: $Cmd" }
}

function Invoke-Scp {
    param([string]$Source, [string]$Dest)
    & scp @sshArgs -r $Source "${DropletHost}:${Dest}"
    if ($LASTEXITCODE -ne 0) { throw "scp failed: $Source -> $Dest" }
}

$publishDir = Join-Path $repoRoot 'artifacts\publish'

if (-not $SkipFrontend) {
    Write-Host "==> Building Angular activity (production)" -ForegroundColor Cyan
    Push-Location (Join-Path $repoRoot 'discord-activity')
    try {
        if (-not (Test-Path 'node_modules')) {
            npm ci
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
        }
        npx ng build --configuration production
        if ($LASTEXITCODE -ne 0) { throw "Angular build failed" }
    } finally {
        Pop-Location
    }
}

Write-Host "==> Publishing .NET app (Release, linux-x64)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
dotnet publish .\LinkshellManagerDiscordApp.csproj `
    -c Release `
    -r linux-x64 `
    --self-contained false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "==> Stopping service on droplet" -ForegroundColor Cyan
Invoke-Ssh "sudo systemctl stop lsmanager || true"

Write-Host "==> Syncing publish output to droplet" -ForegroundColor Cyan
Invoke-Ssh "sudo rm -rf ${RemoteAppDir}.new && sudo mkdir -p ${RemoteAppDir}.new && sudo chown -R `$(id -u):`$(id -g) ${RemoteAppDir}.new"
Invoke-Scp "$publishDir\*" "${RemoteAppDir}.new/"
Invoke-Ssh "sudo chown -R lsmanager:lsmanager ${RemoteAppDir}.new && sudo rm -rf ${RemoteAppDir}.old && (sudo test -d ${RemoteAppDir} && sudo mv ${RemoteAppDir} ${RemoteAppDir}.old || true) && sudo mv ${RemoteAppDir}.new ${RemoteAppDir}"

Write-Host "==> Starting service" -ForegroundColor Cyan
Invoke-Ssh "sudo systemctl start lsmanager"
Start-Sleep -Seconds 3
Invoke-Ssh "sudo systemctl --no-pager --full status lsmanager | head -n 20"

Write-Host "==> Done." -ForegroundColor Green
Write-Host "Tail logs with: ssh $DropletHost 'sudo journalctl -u lsmanager -f'"
