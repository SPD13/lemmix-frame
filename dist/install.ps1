# install.ps1 - installs or updates Lemmix on a Steam Frame, from a Windows 10/11 PC on the same
# network. The Frame needs Developer Mode on; the PC needs nothing more than Windows' own OpenSSH
# client (installed by default on Windows 10 1809 and later, and on Windows 11).
#
#   powershell -ExecutionPolicy Bypass -c "irm https://github.com/SPD13/lemmix-frame/releases/latest/download/install.ps1 | iex"
#   powershell -ExecutionPolicy Bypass -File install.ps1 [-Host ADDRESS] [-File lemmix-frame-linux-arm64.tar.gz] [-Uninstall [-Purge]]
#   powershell -ExecutionPolicy Bypass -c "& ([scriptblock]::Create((irm <url>/install.ps1))) -Uninstall"   (options, no download)
#
# The same steps as install.sh: pair this PC with the Frame the first time (an ssh key made for
# the installer, %LOCALAPPDATA%\lemmix-frame\frame_rsa, accepted in the headset), then run
# frame-install.sh on the Frame over ssh: it downloads the release there, installs it in
# ~/devkit-game/lemmix and adds "lemmix" to the Steam library. Run it again to update.
# Environment: FRAME_HOST, FRAME_USER, FRAME_KEY, LEMMIX_RELEASE_URL (as install.sh).

$BaseUrl = if ($env:LEMMIX_RELEASE_URL) { $env:LEMMIX_RELEASE_URL } else { 'https://github.com/SPD13/lemmix-frame/releases/latest/download' }
$Asset = 'lemmix-frame-linux-arm64.tar.gz'
$NewKey = Join-Path $env:LOCALAPPDATA 'lemmix-frame\frame_rsa'
$DevkitKey = Join-Path $env:LOCALAPPDATA 'steamos-devkit\devkit_rsa'
$ServicePort = 32000
$Here = if ($PSCommandPath) { Split-Path -Parent $PSCommandPath } else { '' }

function Say($text) { Write-Host $text }
function Step($text) { Write-Host ''; Write-Host $text -ForegroundColor Cyan }
function Fail($text) { Write-Host ''; Write-Host "error: $text" -ForegroundColor Red; exit 1 }

function Get-Url($url, $timeout = 5) {
  try { return (Invoke-WebRequest -UseBasicParsing -TimeoutSec $timeout -Uri $url).Content } catch { return $null }
}

function Ssh-Args($key) {
  $a = @('-o', 'ConnectTimeout=10', '-o', 'StrictHostKeyChecking=accept-new')
  if ($key) { $a += @('-i', $key, '-o', 'IdentitiesOnly=yes') }
  return $a
}

# Runs ssh/scp without PowerShell turning their stderr into errors; returns the exit code.
function Invoke-Native($exe, [string[]]$arguments, [switch]$Quiet) {
  $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
  if ($Quiet) { & $exe @arguments 2>$null | Out-Null } else { & $exe @arguments | Out-Host }
  $code = $LASTEXITCODE; $ErrorActionPreference = $old
  return $code
}

function Test-Access($key) {
  $code = Invoke-Native 'ssh' ((Ssh-Args $key) + @('-n', '-o', 'BatchMode=yes', "$script:User@$script:FrameHost", 'true')) -Quiet
  return ($code -eq 0)
}

function Find-Frame {
  if ($script:FrameHost) { return }
  Step 'Looking for your Steam Frame on the network...'
  if (Get-Url "http://frame.local:$ServicePort/login-name") {
    $script:FrameHost = 'frame.local'; Say 'found frame.local'; return
  }
  Say 'not found as frame.local (Developer Mode off, another network, or a different name).'
  $reply = Read-Host "The Frame's name or IP address [frame.local]"
  $script:FrameHost = if ($reply) { $reply } else { 'frame.local' }
}

function Invoke-Pairing {
  Step 'Pairing this PC with the Frame (once)'
  if (-not (Get-Url "http://$($script:FrameHost):$ServicePort/properties.json")) {
    Fail "the Frame at $($script:FrameHost) does not answer on port $ServicePort.
Check that it is on, on the same network as this PC, and that Developer Mode is on
(Settings > System > Developer Mode). See the README's install section."
  }
  if (-not (Test-Path $NewKey)) {
    New-Item -ItemType Directory -Force -Path (Split-Path $NewKey) | Out-Null
    $name = ($env:COMPUTERNAME -replace '[^A-Za-z0-9._-]', '-')
    # ProcessStartInfo passes the command line as written: an empty passphrase survives (PowerShell
    # 5.1 drops empty arguments to native programs).
    $psi = New-Object System.Diagnostics.ProcessStartInfo 'ssh-keygen'
    $psi.Arguments = "-q -t rsa -b 3072 -N `"`" -C lemmix-installer@$name -f `"$NewKey`""
    $psi.UseShellExecute = $false
    $p = [System.Diagnostics.Process]::Start($psi); $p.WaitForExit()
    if ($p.ExitCode -ne 0 -or -not (Test-Path "$NewKey.pub")) { Fail 'ssh-keygen could not make a key' }
  }
  Say 'The Frame will ask, in the headset, whether to allow this PC ("Development host at IP ...").'
  Say "It must be accepted within 30 seconds, from Steam's menus (not in a game)."
  Read-Host 'Press Enter, then put the headset on and accept' | Out-Null
  Say 'sending the pairing request...'
  $pub = [System.IO.File]::ReadAllText("$NewKey.pub")
  try {
    Invoke-WebRequest -UseBasicParsing -TimeoutSec 60 -Method Post -ContentType 'text/plain' `
      -Body $pub -Uri "http://$($script:FrameHost):$ServicePort/register" | Out-Null
  } catch {
    $detail = $_.ErrorDetails.Message; if (-not $detail) { $detail = $_.Exception.Message }
    Fail "pairing failed: $detail
Declined, or no answer within 30 seconds: run the installer again and accept in the headset."
  }
  Say 'paired'
  for ($i = 0; $i -lt 10; $i++) {
    if (Test-Access $NewKey) { $script:Key = $NewKey; return }
    Start-Sleep -Seconds 2
  }
  Fail "paired, but ssh to $($script:User)@$($script:FrameHost) does not work. Try again in a minute."
}

# Runs frame-install.sh on the Frame: this copy if it sits next to this script (copied over, its
# line ends fixed there), else the release's, downloaded by the Frame.
function Invoke-OnFrame([string[]]$installArgs) {
  $quoted = ($installArgs | ForEach-Object { "'$_'" }) -join ' '
  $target = "$($script:User)@$($script:FrameHost)"
  if ($Here -and (Test-Path (Join-Path $Here 'frame-install.sh'))) {
    $code = Invoke-Native 'scp' ((Ssh-Args $script:Key) + @((Join-Path $Here 'frame-install.sh'), "${target}:.lemmix-frame-install.sh"))
    if ($code -ne 0) { Fail 'could not copy frame-install.sh to the Frame' }
    $remote = "tr -d '\r' < .lemmix-frame-install.sh | bash -s -- $quoted; rc=`$?; rm -f .lemmix-frame-install.sh; exit `$rc"
  } else {
    $remote = "set -o pipefail; curl -fsSL '$BaseUrl/frame-install.sh' | tr -d '\r' | bash -s -- $quoted"
  }
  return (Invoke-Native 'ssh' ((Ssh-Args $script:Key) + @('-n', $target, $remote)))
}

function Install-Lemmix($argv) {
  $script:FrameHost = $env:FRAME_HOST
  $file = ''; $mode = 'install'; $purge = $false
  for ($i = 0; $i -lt $argv.Count; $i++) {
    switch -Regex ($argv[$i]) {
      '^--?host$' { $script:FrameHost = $argv[++$i] }
      '^--?file$' { $file = $argv[++$i] }
      '^--?uninstall$' { $mode = 'uninstall' }
      '^--?purge$' { $purge = $true }
      default { Fail "unknown option $($argv[$i]) (see the top of install.ps1)" }
    }
  }
  if ($file -and -not (Test-Path $file)) { Fail "no such file: $file" }
  if (-not (Get-Command ssh -ErrorAction SilentlyContinue)) {
    Fail "Windows' OpenSSH client is missing: Settings > System > Optional features > Add a feature > OpenSSH Client, then run this again."
  }

  Say 'Lemmix for Steam Frame - installer'
  Find-Frame
  $script:FrameHost = $script:FrameHost.TrimEnd('.')
  $script:User = $env:FRAME_USER
  if (-not $script:User) { $script:User = Get-Url "http://$($script:FrameHost):$ServicePort/login-name" }
  if (-not $script:User) { $script:User = 'steamos' }
  $script:User = "$($script:User)".Trim()
  $script:Key = $null
  Step "Connecting to $($script:User)@$($script:FrameHost)"
  foreach ($k in @($env:FRAME_KEY, $NewKey, $DevkitKey, '')) {
    if ($k -and -not (Test-Path $k)) { continue }
    if (Test-Access $k) { $script:Key = $k; break }
  }
  if ($null -ne $script:Key) { Say 'ssh access ok' } else { Invoke-Pairing }

  if ($mode -eq 'uninstall') {
    Step 'Removing Lemmix from the Frame'
    $a = @('--uninstall'); if ($purge) { $a += '--purge' }
    $code = Invoke-OnFrame $a
  } elseif ($file) {
    Step "Uploading $(Split-Path -Leaf $file)"
    $code = Invoke-Native 'ssh' ((Ssh-Args $script:Key) + @('-n', "$($script:User)@$($script:FrameHost)", 'mkdir -p devkit-game'))
    $code = Invoke-Native 'scp' ((Ssh-Args $script:Key) + @($file, "$($script:User)@$($script:FrameHost):devkit-game/.lemmix-upload.tar.gz"))
    if ($code -ne 0) { Fail 'the upload failed' }
    Step 'Installing on the Frame'
    $code = Invoke-OnFrame @('--file', 'devkit-game/.lemmix-upload.tar.gz')
    Invoke-Native 'ssh' ((Ssh-Args $script:Key) + @('-n', "$($script:User)@$($script:FrameHost)", 'rm -f devkit-game/.lemmix-upload.tar.gz')) | Out-Null
  } else {
    Step 'Installing on the Frame (it downloads the latest release itself)'
    $code = Invoke-OnFrame @('--url', "$BaseUrl/$Asset")
  }
  if ($code -ne 0) { exit $code }
}

Install-Lemmix $args
