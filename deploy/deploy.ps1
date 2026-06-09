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

# Sync via a tarball, NOT `scp -r publish\*`: that glob skips dot-directories (so
# the Playwright driver folder `.playwright` never copied) and scp from Windows
# drops the Linux executable bit. `tar -C publish .` captures hidden entries, and
# we restore +x on the node driver after extraction.
Write-Host "==> Packaging publish output (tarball keeps .playwright + perms)" -ForegroundColor Cyan
$tarball = Join-Path $repoRoot 'artifacts\publish.tar.gz'
if (Test-Path $tarball) { Remove-Item -Force $tarball }
& tar -czf $tarball -C $publishDir .
if ($LASTEXITCODE -ne 0) { throw "tar failed packaging $publishDir" }

Write-Host "==> Syncing to droplet" -ForegroundColor Cyan
Invoke-Ssh "sudo rm -rf ${RemoteAppDir}.new /tmp/lsm-publish.tar.gz && sudo mkdir -p ${RemoteAppDir}.new"
Invoke-Scp $tarball "/tmp/lsm-publish.tar.gz"
# Extract, restore +x on the Playwright node driver (tar-from-Windows stores no
# exec bit), chown to the service user, then swap the dir into place atomically.
Invoke-Ssh "sudo tar -xzf /tmp/lsm-publish.tar.gz -C ${RemoteAppDir}.new && sudo chmod +x ${RemoteAppDir}.new/.playwright/node/*/node && sudo chmod +x ${RemoteAppDir}.new/LinkshellManagerDiscordApp 2>/dev/null; sudo chown -R lsmanager:lsmanager ${RemoteAppDir}.new && sudo rm -rf ${RemoteAppDir}.old && (sudo test -d ${RemoteAppDir} && sudo mv ${RemoteAppDir} ${RemoteAppDir}.old || true) && sudo mv ${RemoteAppDir}.new ${RemoteAppDir} && sudo rm -f /tmp/lsm-publish.tar.gz"

Write-Host "==> Starting service" -ForegroundColor Cyan
Invoke-Ssh "sudo systemctl start lsmanager"

Write-Host "==> Verifying service health" -ForegroundColor Cyan
# Hard check: the unit must come up 'active', else fail the deploy loudly with the
# status + recent log so a crash-on-startup (e.g. a bad migration) is obvious.
$active = ''
for ($i = 0; $i -lt 6; $i++) {
    Start-Sleep -Seconds 2
    $active = (& ssh @sshArgs $DropletHost "systemctl is-active lsmanager" | Out-String).Trim()
    if ($active -eq 'active') { break }
}
if ($active -ne 'active') {
    Invoke-Ssh "sudo systemctl --no-pager --full status lsmanager | head -n 30"
    throw "lsmanager is '$active' after deploy (expected 'active'). See the status above and 'journalctl -u lsmanager'."
}
Write-Host "    service is active." -ForegroundColor Green
Invoke-Ssh "sudo systemctl --no-pager --full status lsmanager | head -n 12"

# Soft check: surface the event-board image renderer status. The Chromium
# auto-install runs on a background task and may still be downloading on a fresh
# host, so this is informational only and never fails the deploy.
Write-Host "==> Event-board image renderer (Playwright/Chromium)" -ForegroundColor Cyan
$pw = & ssh @sshArgs $DropletHost "sudo journalctl -u lsmanager --since '5 min ago' --no-pager | grep -i -E 'playwright|chromium|board image' | tail -n 3"
if ($pw) {
    $pw | ForEach-Object { Write-Host "    $_" -ForegroundColor Green }
} else {
    Write-Host "    No Playwright log lines yet - Chromium may still be downloading on first boot." -ForegroundColor Yellow
    Write-Host "    Boards post as the text-embed fallback until it's ready. Re-check with:" -ForegroundColor Yellow
    Write-Host "      ssh $DropletHost 'sudo journalctl -u lsmanager | grep -i playwright'" -ForegroundColor Yellow
}

Write-Host "==> Done." -ForegroundColor Green
Write-Host "Tail logs with: ssh $DropletHost 'sudo journalctl -u lsmanager -f'"
