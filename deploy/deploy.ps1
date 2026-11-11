# Deploy LSManager to the droplet.
# Usage:
#   .\deploy\deploy.ps1 -DropletHost root@1.2.3.4
#   .\deploy\deploy.ps1 -DropletHost lsmanager-deploy@1.2.3.4 -SshKey "$HOME\.ssh\id_ed25519"
#
# Switches (each skips a safety check, so use them only when you mean it):
#   -AllowDirty   deploy even with uncommitted changes (the version shows "-dirty")
#   -SkipTests    skip "dotnet test" (e.g. a known-failing test you have not fixed yet)
#   -SkipBackup   skip the pre-deploy database dump (e.g. the dump itself is broken)
#   -SkipFrontend skip the Angular build
#
# Requires: dotnet 8 SDK, git, ssh + scp on PATH (Windows 10+ has these built in),
#           Angular CLI deps already installed in discord-activity\.
#           Docker Desktop running (the Postgres tests need it; not needed with -SkipTests).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$DropletHost,

    [string]$SshKey,

    [string]$RemoteAppDir = "/var/www/lsmanager",

    [switch]$SkipFrontend,

    [switch]$AllowDirty,

    [switch]$SkipTests,

    [switch]$SkipBackup
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

# A deploy publishes the working tree, so uncommitted edits would go live without
# being in git, and nobody could tell which code is running. Refuse unless asked.
Write-Host "==> Checking git working tree" -ForegroundColor Cyan
$gitChanges = (& git status --porcelain | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "git status failed" }
$version = (& git rev-parse --short HEAD | Out-String).Trim()
if ($LASTEXITCODE -ne 0) { throw "git rev-parse failed" }
if ($gitChanges) {
    if (-not $AllowDirty) {
        Write-Host $gitChanges
        throw "Uncommitted changes (listed above). Commit them first, or pass -AllowDirty to deploy them anyway."
    }
    Write-Warning "Deploying with uncommitted changes (-AllowDirty)."
    $version = "$version-dirty"
}
Write-Host "    version: $version"

# A broken test or a forgotten migration should stop here, not in production.
if (-not $SkipTests) {
    # The Postgres tests (tests\LinkshellManager.PostgresTests) start a database in Docker. Without
    # Docker every one of them fails, and "Tests failed ... pass -SkipTests" would tempt skipping the
    # unit tests too. Check first and say what is actually wrong. cmd /c keeps docker's stderr away
    # from PowerShell, which would otherwise treat any warning line as an error here.
    cmd /c "docker info >nul 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "Docker is not running. Start Docker Desktop (the Postgres tests need it), then deploy again." }

    Write-Host "==> Running tests" -ForegroundColor Cyan
    dotnet test .\LinkshellManagerDiscordApp.sln --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "Tests failed. Fix them, or pass -SkipTests if you are sure the failure is harmless." }
} else {
    Write-Warning "Skipping tests (-SkipTests)."
}

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

# /healthz reads this back (Utils/AppVersion.cs), so you can see which commit is live.
Set-Content -Path (Join-Path $publishDir 'version.txt') -Value $version -Encoding Ascii

# Sync via a tarball, NOT `scp -r publish\*`: that glob skips dot-directories.
# `tar -C publish .` captures hidden entries, and we restore +x on the app host
# after extraction (neither scp nor tar from Windows keeps the Linux exec bit).
Write-Host "==> Packaging publish output" -ForegroundColor Cyan
$tarball = Join-Path $repoRoot 'artifacts\publish.tar.gz'
if (Test-Path $tarball) { Remove-Item -Force $tarball }
& tar -czf $tarball -C $publishDir .
if ($LASTEXITCODE -ne 0) { throw "tar failed packaging $publishDir" }

# Upload and unpack into ${RemoteAppDir}.new while the old build keeps running, so
# the service is only down for the stop -> swap -> start at the end.
Write-Host "==> Syncing to droplet" -ForegroundColor Cyan
Invoke-Ssh "sudo rm -rf ${RemoteAppDir}.new /tmp/lsm-publish.tar.gz /tmp/lsm-backup.sh && sudo mkdir -p ${RemoteAppDir}.new"
Invoke-Scp $tarball "/tmp/lsm-publish.tar.gz"
Invoke-Scp (Join-Path $repoRoot 'deploy\lsm-backup.sh') "/tmp/lsm-backup.sh"
# Extract, restore +x on the app host (tar-from-Windows stores no exec bit) and chown
# to the service user.
Invoke-Ssh "sudo tar -xzf /tmp/lsm-publish.tar.gz -C ${RemoteAppDir}.new && (sudo chmod +x ${RemoteAppDir}.new/LinkshellManagerDiscordApp 2>/dev/null || true) && sudo chown -R lsmanager:lsmanager ${RemoteAppDir}.new && sudo rm -f /tmp/lsm-publish.tar.gz"
# Install the backup script from the repo each time, so the droplet copy never drifts.
# sed strips Windows line endings, which would break a bash script.
Invoke-Ssh 'sudo sed -i ''s/\r$//'' /tmp/lsm-backup.sh && sudo install -m 700 -o root -g root /tmp/lsm-backup.sh /usr/local/bin/lsm-backup.sh && sudo rm -f /tmp/lsm-backup.sh'

# The new build migrates the database on startup, and some migrations delete data.
# Dump it first; the script fails (and so stops the deploy) if the dump looks empty.
if (-not $SkipBackup) {
    Write-Host "==> Backing up the database (/usr/local/bin/lsm-backup.sh)" -ForegroundColor Cyan
    & ssh @sshArgs $DropletHost "sudo /usr/local/bin/lsm-backup.sh"
    if ($LASTEXITCODE -ne 0) {
        throw "Database backup failed, so nothing was switched: the old build is still running. Fix the backup (see deploy/RUNBOOK.md, 'Database backups'), or pass -SkipBackup."
    }
} else {
    Write-Warning "Skipping the database backup (-SkipBackup)."
}

Write-Host "==> Stopping service and swapping in the new build" -ForegroundColor Cyan
Invoke-Ssh "sudo systemctl stop lsmanager || true"
Invoke-Ssh "sudo rm -rf ${RemoteAppDir}.old && (sudo test -d ${RemoteAppDir} && sudo mv ${RemoteAppDir} ${RemoteAppDir}.old || true) && sudo mv ${RemoteAppDir}.new ${RemoteAppDir}"

Write-Host "==> Starting service" -ForegroundColor Cyan
Invoke-Ssh "sudo systemctl start lsmanager"

# 'systemctl is-active' says 'active' even while the app crash-loops (Restart=always),
# so ask the app itself. Kestrel only starts listening after the startup migrations,
# and /readyz also checks the database. Poll for about 90 seconds. The X-Forwarded-Proto
# header makes the request look like it came through nginx, so it is never redirected.
Write-Host "==> Waiting for /readyz (up to ~90 s)" -ForegroundColor Cyan
$readyCmd = 'for i in $(seq 1 30); do if curl -fsS --max-time 2 -H ''X-Forwarded-Proto: https'' http://127.0.0.1:5000/readyz 2>/dev/null | grep -qx Healthy; then exit 0; fi; sleep 3; done; exit 1'
& ssh @sshArgs $DropletHost $readyCmd
if ($LASTEXITCODE -ne 0) {
    Write-Host "Recent log:" -ForegroundColor Yellow
    & ssh @sshArgs $DropletHost "sudo journalctl -u lsmanager -n 50 --no-pager"
    throw "lsmanager did not become ready after deploy. See the log above. To roll back, see deploy/RUNBOOK.md, 'Rollback'."
}
Write-Host "    app is ready." -ForegroundColor Green

# Confirm the droplet is running the commit we just built.
$health = (& ssh @sshArgs $DropletHost 'curl -fsS -H ''X-Forwarded-Proto: https'' http://127.0.0.1:5000/healthz' | Out-String).Trim()
Write-Host "    /healthz: $health"
try {
    $liveVersion = ($health | ConvertFrom-Json).version
} catch {
    $liveVersion = $null
}
if ($liveVersion -ne $version) {
    Write-Warning "Expected version '$version' but /healthz says '$liveVersion'."
}

Write-Host "==> Done." -ForegroundColor Green
Write-Host "Tail logs with: ssh $DropletHost 'sudo journalctl -u lsmanager -f'"
