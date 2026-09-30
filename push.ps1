#requires -Version 7.0

<#
.SYNOPSIS
Build local working files on GitHub Actions, then deploy to the Pi.
.DESCRIPTION
Requires Git for Windows, GitHub CLI, and Windows
OpenSSH ssh/scp/ssh-keygen. Git push authentication and a Git author identity must be set up.
Expects a Pi flashed from the release image, with user "pi" and rt4k_pi installed in /opt/rt4k_pi.
The first run (and the first run after reflashing) asks you to confirm the Pi's host key fingerprint and
prompts for the pi password to install a dedicated SSH key and enable passwordless sudo.
#>
[CmdletBinding()]
param(
	[ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_./-]*$')]
	[string]$Remote = 'origin',
	[ValidatePattern('^([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+)?$')]
	[string]$Repository = '',
	[ValidatePattern('^[A-Za-z0-9][A-Za-z0-9.-]*$')]
	[string]$PiHost = 'rt4k.local',
	[ValidateRange(30, 3600)]
	[int]$RunStartTimeoutSeconds = 180,
	[ValidateRange(60, 7200)]
	[int]$BuildTimeoutSeconds = 2400,
	[ValidateRange(1, 60)]
	[int]$PollSeconds = 5,
	[switch]$NoFollow
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = $PSScriptRoot
$workflow = 'ci.yml'
$artifact = 'rt4k_pi-linux-arm64'
$Pi = "pi@$PiHost"
$piInstallDir = '/opt/rt4k_pi'
$sshDirectory = Join-Path $HOME '.ssh'
$piKey = Join-Path $sshDirectory 'rt4k_pi_ed25519'
$piKnownHosts = Join-Path $sshDirectory 'rt4k_pi_known_hosts'
$sshCommonOptions = @('-o', "UserKnownHostsFile=$piKnownHosts", '-o', 'StrictHostKeyChecking=yes',
	'-o', 'UpdateHostKeys=no', '-o', 'ConnectTimeout=15')
$sshOptions = $sshCommonOptions + @('-i', $piKey, '-o', 'IdentitiesOnly=yes', '-o', 'BatchMode=yes',
	'-o', 'PreferredAuthentications=publickey')

function Invoke-Tool {
	param(
		[string]$Tool,
		[string[]]$Arguments,
		[switch]$Capture,
		[switch]$ReturnExitCode,
		[switch]$Interactive,
		[int]$TimeoutSeconds = 120,
		[hashtable]$Environment = @{}
	)

	$start = [Diagnostics.ProcessStartInfo]::new()
	$start.FileName = (Get-Command $Tool -CommandType Application -ErrorAction Stop).Source
	$start.WorkingDirectory = $repositoryRoot
	$start.UseShellExecute = $false
	$start.RedirectStandardOutput = $Capture.IsPresent
	$start.RedirectStandardError = $Capture.IsPresent
	if ($Tool -eq 'gh') {
		$start.Environment['GH_HOST'] = 'github.com'
		if ($Interactive) {
			$null = $start.Environment.Remove('GH_PROMPT_DISABLED')
		}
		else {
			$start.Environment['GH_PROMPT_DISABLED'] = '1'
		}
	}
	foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
	foreach ($key in $Environment.Keys) { $start.Environment[$key] = $Environment[$key] }
	$process = [Diagnostics.Process]::new()
	$process.StartInfo = $start
	$started = $false
	try {
		$null = $process.Start()
		$started = $true
		if ($Capture) {
			$stdout = $process.StandardOutput.ReadToEndAsync()
			$stderr = $process.StandardError.ReadToEndAsync()
		}
		$timer = [Diagnostics.Stopwatch]::StartNew()
		while (-not $process.WaitForExit(200)) {
			if ($TimeoutSeconds -gt 0 -and $timer.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
				throw "$Tool timed out after $TimeoutSeconds seconds."
			}
		}
		if ($Capture) {
			$output = $stdout.GetAwaiter().GetResult()
			$errorOutput = $stderr.GetAwaiter().GetResult()
		}
		if ($ReturnExitCode) { return $process.ExitCode }
		if ($process.ExitCode -ne 0) {
			$detail = if ($Capture) { $errorOutput.Trim() } else { 'See output above.' }
			throw "$Tool failed (exit $($process.ExitCode)): $detail"
		}
		if ($Capture) { return $output.TrimEnd("`r", "`n") }
	}
	finally {
		if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
		$process.Dispose()
	}
}

function Initialize-PiAccess {
	if (-not (Test-Path -LiteralPath $sshDirectory)) { $null = New-Item -ItemType Directory -Path $sshDirectory }
	if (-not (Test-Path -LiteralPath $piKey)) {
		Write-Host "Creating SSH key $piKey..."
		Invoke-Tool ssh-keygen @('-q', '-t', 'ed25519', '-N', '', '-C', 'rt4k_pi-push', '-f', $piKey) -Capture | Out-Null
	}

	# Windows ssh-keyscan aborts on key exchange methods it doesn't know, so let ssh record the
	# host key into a scratch file instead. Authentication is expected to fail; only the key matters.
	$scanFile = Join-Path ([IO.Path]::GetTempPath()) "rt4k_pi-hostkey-$([Guid]::NewGuid().ToString('N'))"
	try {
		$null = Invoke-Tool ssh @('-o', "UserKnownHostsFile=$scanFile", '-o', 'GlobalKnownHostsFile=NUL',
			'-o', 'StrictHostKeyChecking=accept-new', '-o', 'HashKnownHosts=no', '-o', 'HostKeyAlgorithms=ssh-ed25519',
			'-o', 'BatchMode=yes', '-o', 'PreferredAuthentications=none', '-o', 'ConnectTimeout=15',
			'-n', '-T', "nobody@$PiHost", 'true') -Capture -ReturnExitCode -TimeoutSeconds 30
		$scanned = @(if (Test-Path -LiteralPath $scanFile) {
			Get-Content -LiteralPath $scanFile | Where-Object { $_ -match '^\S+\s+ssh-ed25519\s+\S+' }
		})
	}
	finally { Remove-Item -LiteralPath $scanFile -Force -ErrorAction SilentlyContinue }
	if ($scanned.Count -ne 1) { throw "Could not read the SSH host key from $PiHost. Is the Pi powered on and on the network?" }
	$hostKey = ($scanned[0] -split '\s+')[2]
	$known = @()
	if (Test-Path -LiteralPath $piKnownHosts) { $known = @(Get-Content -LiteralPath $piKnownHosts) }
	$current = @($known | Where-Object { ($_ -split '\s+')[0] -ceq $PiHost })
	$trusted = $current.Count -eq 1 -and ($current[0] -split '\s+')[1] -eq 'ssh-ed25519' -and ($current[0] -split '\s+')[2] -ceq $hostKey
	if (-not $trusted) {
		$fingerprint = 'SHA256:' + [Convert]::ToBase64String(
			[Security.Cryptography.SHA256]::HashData([Convert]::FromBase64String($hostKey))).TrimEnd('=')
		if ($current.Count) { Write-Warning "The SSH host key for $PiHost has changed (expected after reflashing the Pi)." }
		Write-Host "Host key fingerprint for ${PiHost}: ED25519 $fingerprint"
		$answer = Read-Host 'Trust this host key? (yes/no)'
		if ($answer -ne 'yes') { throw 'Host key not trusted; nothing was deployed.' }
		$kept = @($known | Where-Object { ($_ -split '\s+')[0] -cne $PiHost })
		Set-Content -LiteralPath $piKnownHosts -Value ($kept + "$PiHost ssh-ed25519 $hostKey")
	}

	Write-Host "Checking key-only SSH and passwordless sudo on $Pi..."
	if ((Invoke-Tool ssh ($sshOptions + @('-n', '-T', $Pi, 'sudo -n true')) -ReturnExitCode -TimeoutSeconds 30) -eq 0) { return }

	Write-Host "Setting up SSH key login and passwordless sudo on $Pi. Enter the pi password when asked (twice: SSH, then sudo)."
	$publicKey = (Get-Content -LiteralPath "$piKey.pub" -Raw).Trim()
	if ($publicKey -notmatch '^ssh-ed25519 [A-Za-z0-9+/=]+ rt4k_pi-push$') { throw "Unexpected public key format in $piKey.pub." }
	$setup = "set -e; umask 077; mkdir -p ~/.ssh; touch ~/.ssh/authorized_keys; " +
		"grep -qxF '$publicKey' ~/.ssh/authorized_keys || echo '$publicKey' >> ~/.ssh/authorized_keys; " +
		"chmod 700 ~/.ssh; chmod 600 ~/.ssh/authorized_keys; t=`$(mktemp); trap 'rm -f `$t' EXIT; " +
		"echo 'pi ALL=(ALL) NOPASSWD: ALL' > `$t; sudo visudo -cf `$t >/dev/null; " +
		"sudo install -m 0440 -o root -g root `$t /etc/sudoers.d/010_rt4k_pi-nopasswd"
	Invoke-Tool ssh ($sshCommonOptions + @('-o', 'PubkeyAuthentication=no',
		'-o', 'PreferredAuthentications=password,keyboard-interactive', '-t', $Pi, $setup)) -TimeoutSeconds 300
	if ((Invoke-Tool ssh ($sshOptions + @('-n', '-T', $Pi, 'sudo -n true')) -ReturnExitCode -TimeoutSeconds 30) -ne 0) {
		throw "Key login or passwordless sudo still does not work on $Pi after setup."
	}
}

function Get-SnapshotRun {
	$runs = Invoke-Tool gh @('run', 'list', '--repo', $Repository, '--workflow', $workflow,
		'--branch', $branch, '--commit', $snapshot, '--event', 'push', '--limit', '100',
		'--json', 'databaseId,headBranch,headSha,event,status,conclusion,url,workflowDatabaseId') -Capture -TimeoutSeconds 30 | ConvertFrom-Json
	$matching = @($runs | Where-Object {
		$_ -and $_.headSha -eq $snapshot -and $_.headBranch -ceq $branch -and
		$_.event -eq 'push' -and $_.workflowDatabaseId -eq $workflowInfo.id
	})
	if ($matching.Count -gt 1) { throw "Multiple matching runs found for $branch; inspect Actions before proceeding." }
	if ($matching.Count -eq 1) { return $matching[0] }
}

function Clear-RemoteBuild {
	if (-not $pushAttempted -or $cleanupState.Finished) { return }
	if (-not $cleanupState.RunDeleted) {
		try {
			$cleanupRun = $null
			$timer = [Diagnostics.Stopwatch]::StartNew()
			do {
				$cleanupRun = Get-SnapshotRun
				if ($cleanupRun) { break }
				Start-Sleep -Seconds $PollSeconds
			} while ($timer.Elapsed.TotalSeconds -lt [Math]::Min(60, $RunStartTimeoutSeconds))
			if (-not $cleanupRun) {
				throw "No matching run is visible yet. Check Actions for branch $branch, commit $snapshot; a delayed run may still appear."
			}
			$cleanupId = [string]$cleanupRun.databaseId
			if ($runId -and $cleanupId -ne $runId) { throw "Run ID changed; refusing to delete $cleanupId." }
			if ($cleanupRun.status -ne 'completed') {
				Write-Host "Canceling CI run $cleanupId..."
				Invoke-Tool gh @('run', 'cancel', $cleanupId, '--repo', $Repository) -Capture | Out-Null
				$timer.Restart()
				do {
					$cleanupRun = Get-SnapshotRun
					if ($cleanupRun -and [string]$cleanupRun.databaseId -eq $cleanupId -and $cleanupRun.status -eq 'completed') { break }
					Start-Sleep -Seconds $PollSeconds
				} while ($timer.Elapsed.TotalSeconds -lt 60)
				if (-not $cleanupRun -or [string]$cleanupRun.databaseId -ne $cleanupId -or $cleanupRun.status -ne 'completed') {
					throw "Run $cleanupId has not finished canceling; remove it from Actions once it stops."
				}
			}
			Invoke-Tool gh @('run', 'delete', $cleanupId, '--repo', $Repository) -Capture | Out-Null
			$cleanupState.RunDeleted = $true
			Write-Host "Removed CI run $cleanupId and its artifacts."
		}
		catch { Write-Warning "GitHub run cleanup failed in $Repository ($branch, $snapshot): $_" }
	}
	if (-not $cleanupState.RefDeleted) {
		try {
			$remoteValue = Invoke-Tool git @('ls-remote', '--refs', $pushUrls[0], $remoteRef) -Capture
			if ($remoteValue) {
				if ($remoteValue -cne "$snapshot`t$remoteRef") { throw 'The remote ref no longer points to our snapshot; it will not be deleted.' }
				Invoke-Tool git @('push', '--porcelain', "--force-with-lease=${remoteRef}:$snapshot", $Remote, ":$remoteRef") -Capture | Out-Null
			}
			$cleanupState.RefDeleted = $true
			Write-Host "Removed temporary branch $branch."
		}
		catch { Write-Warning "Branch cleanup failed in $Repository for $remoteRef ($snapshot): $_" }
	}
	$cleanupState.Finished = $cleanupState.RunDeleted -and $cleanupState.RefDeleted
}

$tempDirectory = $null
$pushAttempted = $false
$runId = $null
$cleanupState = @{ RunDeleted = $false; RefDeleted = $false; Finished = $false }
$piUploadAttempted = $false
try {
	foreach ($tool in 'git', 'gh', 'ssh', 'scp', 'ssh-keygen') {
		$null = Get-Command $tool -CommandType Application -ErrorAction Stop
	}
	foreach ($variable in 'GIT_INDEX_FILE', 'GIT_DIR', 'GIT_WORK_TREE') {
		if ([Environment]::GetEnvironmentVariable($variable)) {
			throw "Unset $variable before running this script; it requires the normal working tree."
		}
	}
	$repositoryRoot = Invoke-Tool git @('rev-parse', '--show-toplevel') -Capture
	$head = Invoke-Tool git @('rev-parse', '--verify', 'HEAD') -Capture
	$sparse = Invoke-Tool git @('config', '--type=bool', '--default=false', '--get', 'core.sparseCheckout') -Capture
	if ($sparse -eq 'true') { throw 'Sparse checkouts are not supported for working-tree snapshots.' }
	$entries = Invoke-Tool git @('ls-files', '--stage') -Capture
	if ($entries -match '(?m)^160000 ') { throw 'Submodules are not supported for working-tree snapshots.' }

	$pushUrls = @( (Invoke-Tool git @('remote', 'get-url', '--push', '--all', $Remote) -Capture) -split '\r?\n' )
	if ($pushUrls.Count -ne 1) { throw 'The build remote must have exactly one push URL.' }
	if ($pushUrls[0] -notmatch '^(?:https://github\.com/|git@github\.com:|ssh://git@github\.com/)(?<repo>[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?/?$') {
		throw 'The build remote must use a standard github.com HTTPS or SSH URL.'
	}
	$remoteRepository = $Matches.repo
	if ($Repository -and $Repository -ine $remoteRepository) {
		throw "Repository '$Repository' does not match remote '$Remote' ($remoteRepository)."
	}
	$Repository = $remoteRepository
	Initialize-PiAccess
	$authArguments = @('auth', 'status', '--hostname', 'github.com', '--active')
	$authExitCode = Invoke-Tool gh $authArguments -ReturnExitCode
	if ($authExitCode -in 1, 4) {
		if ($env:GH_TOKEN -or $env:GITHUB_TOKEN) {
			throw 'GitHub authentication failed with an environment-provided token. Correct or unset GH_TOKEN/GITHUB_TOKEN before trying again; interactive login cannot override it.'
		}
		Write-Host 'GitHub sign-in is required. Complete the GitHub CLI login prompts to continue...'
		Invoke-Tool gh @('auth', 'login', '--hostname', 'github.com') -Interactive -TimeoutSeconds 0
		Invoke-Tool gh $authArguments
	}
	elseif ($authExitCode -ne 0) {
		throw "GitHub authentication check failed (exit $authExitCode); login was not started."
	}
	$workflowInfo = Invoke-Tool gh @('api', "repos/$Repository/actions/workflows/$workflow") -Capture | ConvertFrom-Json
	if ($workflowInfo.state -ne 'active') { throw "Workflow $workflow is not active in $Repository." }

	$invocationId = [Guid]::NewGuid().ToString('N')
	$branch = "dev-build/$invocationId"
	$remoteRef = "refs/heads/$branch"
	$tempDirectory = Join-Path ([IO.Path]::GetTempPath()) "rt4k-push-$invocationId"
	$null = New-Item -ItemType Directory -Path $tempDirectory
	$indexEnvironment = @{ GIT_INDEX_FILE = (Join-Path $tempDirectory 'index') }
	Invoke-Tool git @('read-tree', $head) -Environment $indexEnvironment
	Invoke-Tool git @('add', '--all', '--', '.') -Environment $indexEnvironment
	$tree = Invoke-Tool git @('write-tree') -Capture -Environment $indexEnvironment
	$paths = (Invoke-Tool git @('-c', 'core.quotePath=false', 'ls-tree', '-r', '--name-only', $tree) -Capture) -split '\r?\n'
	$unsafePaths = @($paths | Where-Object {
		$_ -match '(^|/)(bin|obj|\.vs|publish_output|firmware-update)/|(^|/)(settings\.json|push|watch|stop|PROTOCOL[^/]*\.md|\.env(\..*)?|id_rsa|id_ed25519)$|\.(pfx|p12|pem|key)$'
	})
	if ($unsafePaths.Count) { throw "Snapshot contains private/generated files. Remove them from the snapshot inputs first: $($unsafePaths -join ', ')" }
	$workflowPath = '.github/workflows/ci.yml'
	$originalWorkflow = Invoke-Tool git @('rev-parse', "${head}:$workflowPath") -Capture
	$snapshotWorkflow = Invoke-Tool git @('rev-parse', "${tree}:$workflowPath") -Capture
	if ($snapshotWorkflow -ne $originalWorkflow) {
		throw 'Commit CI workflow changes before using this script, so local edits cannot disable its snapshot build.'
	}
	Write-Warning "Uploading working files to $Repository. Public snapshots remain potentially accessible even after branch deletion. Ignore rules are not a secret scanner."
	Invoke-Tool git @('diff', '--stat', $head, $tree)
	$snapshot = Invoke-Tool git @('commit-tree', $tree, '-p', $head, '-m', "Local development build $invocationId") -Capture
	Write-Host "Prepared snapshot $snapshot for $branch."

	$existingRef = Invoke-Tool git @('ls-remote', '--refs', $pushUrls[0], $remoteRef) -Capture
	if ($existingRef) { throw "Ref $remoteRef already exists; it will not be overwritten." }
	$pushAttempted = $true
	Invoke-Tool git @('push', '--porcelain', "--force-with-lease=${remoteRef}:", $Remote, "${snapshot}:$remoteRef")

	Write-Host 'Waiting for GitHub to create the snapshot CI run...'
	$timer = [Diagnostics.Stopwatch]::StartNew()
	$run = $null
	while ($timer.Elapsed.TotalSeconds -lt $RunStartTimeoutSeconds) {
		$run = Get-SnapshotRun
		if ($run) { break }
		Start-Sleep -Seconds $PollSeconds
	}
	if (-not $run) { throw "No CI run appeared for $branch within $RunStartTimeoutSeconds seconds. Check workflow push triggers, Actions permissions, and branch rules." }
	$runId = [string]$run.databaseId
	Write-Host "Build: $($run.url)"
	$timer.Restart()
	$lastStatus = ''
	while ($run.status -ne 'completed') {
		if ($run.status -ne $lastStatus) { Write-Host "CI status: $($run.status)"; $lastStatus = $run.status }
		if ($timer.Elapsed.TotalSeconds -ge $BuildTimeoutSeconds) { throw "Build $runId exceeded $BuildTimeoutSeconds seconds." }
		Start-Sleep -Seconds $PollSeconds
		$run = Get-SnapshotRun
		if (-not $run -or [string]$run.databaseId -ne $runId) { throw "CI run $runId disappeared or changed unexpectedly." }
	}
	if ($run.conclusion -ne 'success') {
		try { Invoke-Tool gh @('run', 'view', $runId, '--repo', $Repository, '--log-failed') }
		catch { Write-Warning "Could not retrieve failed build logs: $_" }
		throw "CI build $runId finished with '$($run.conclusion)': $($run.url)"
	}

	$downloadDirectory = Join-Path $tempDirectory 'artifact'
	Invoke-Tool gh @('run', 'download', $runId, '--repo', $Repository, '--name', $artifact, '--dir', $downloadDirectory) -TimeoutSeconds 300
	$binary = Join-Path $downloadDirectory 'rt4k_pi'
	$hashFile = Join-Path $downloadDirectory 'rt4k_pi.sha256'
	$downloadedFiles = @(Get-ChildItem -LiteralPath $downloadDirectory -Recurse -Force)
	$expectedNames = @('rt4k_pi', 'rt4k_pi.gz', 'rt4k_pi.sha256')
	$unexpected = @($downloadedFiles | Where-Object { $_.PSIsContainer -or $_.DirectoryName -ne (Get-Item -LiteralPath $downloadDirectory).FullName -or $expectedNames -cnotcontains $_.Name })
	$missing = @($expectedNames | Where-Object { -not (Test-Path -LiteralPath (Join-Path $downloadDirectory $_) -PathType Leaf) })
	if ($unexpected.Count -gt 0 -or $missing.Count -gt 0) {
		$details = @()
		if ($missing.Count -gt 0) { $details += "missing: $($missing -join ', ')" }
		if ($unexpected.Count -gt 0) { $details += "unexpected: $(($unexpected | ForEach-Object { $_.Name }) -join ', ')" }
		throw "The downloaded artifact must contain only rt4k_pi, rt4k_pi.gz, and rt4k_pi.sha256 ($($details -join '; '))."
	}
	$expectedHash = ((Get-Content -LiteralPath $hashFile -Raw).Trim() -split '\s+')[0]
	if ($expectedHash -notmatch '^[0-9a-fA-F]{64}$') { throw 'rt4k_pi.sha256 does not contain a valid SHA-256.' }
	$actualHash = (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash
	if (-not [string]::Equals($expectedHash, $actualHash, [StringComparison]::OrdinalIgnoreCase)) {
		throw "The downloaded rt4k_pi does not match rt4k_pi.sha256 (expected $expectedHash, got $actualHash)."
	}
	$stream = [IO.File]::OpenRead($binary)
	try {
		$header = [byte[]]::new(20)
		if ($stream.Read($header, 0, 20) -ne 20 -or $header[0] -ne 0x7f -or
			$header[1] -ne 0x45 -or $header[2] -ne 0x4c -or $header[3] -ne 0x46 -or
			$header[4] -ne 2 -or $header[5] -ne 1 -or $header[18] -ne 183 -or $header[19] -ne 0) {
			throw 'The downloaded rt4k_pi is not a Linux ARM64 ELF executable.'
		}
	}
	finally { $stream.Dispose() }
	Write-Host 'Downloaded and validated the snapshot executable.'

	Clear-RemoteBuild
	if (-not $cleanupState.Finished) { throw 'Build succeeded, but remote cleanup was incomplete. Resolve the warnings above before deploying.' }

	$piUpload = ".rt4k_pi-$invocationId"
	$piStaged = "$piInstallDir/rt4k_pi.new"
	$piUploadAttempted = $true
	Write-Host "Copying executable to $Pi..."
	Invoke-Tool scp ($sshOptions + @($binary, "${Pi}:$piUpload")) -TimeoutSeconds 300
	Write-Host 'Installing and restarting rt4k_pi on the Pi...'
	Invoke-Tool ssh ($sshOptions + @('-n', '-T', $Pi,
		"sudo install -D -m 0755 -- $piUpload $piStaged && sudo mv -f -- $piStaged $piInstallDir/rt4k_pi && rm -f -- $piUpload && sudo systemctl restart rt4k")) -TimeoutSeconds 120
	$piUploadAttempted = $false
	if (-not $NoFollow) {
		Write-Host 'Following the Pi journal (Ctrl+C to stop)...'
		Invoke-Tool ssh ($sshOptions + @('-t', $Pi,
			'journalctl --lines 50 -xefu rt4k --output cat')) -TimeoutSeconds 0
	}
}
finally {
	Clear-RemoteBuild
	if ($piUploadAttempted) {
		try {
			Invoke-Tool ssh ($sshOptions + @('-n', $Pi,
				"rm -f -- $piUpload; sudo -n rm -f -- $piStaged")) -Capture -TimeoutSeconds 30 | Out-Null
		}
		catch { Write-Warning "Could not remove temporary upload on ${Pi}: $_" }
	}
	if ($tempDirectory -and (Test-Path -LiteralPath $tempDirectory)) {
		try { Remove-Item -LiteralPath $tempDirectory -Recurse -Force }
		catch { Write-Warning "Could not remove temporary directory ${tempDirectory}: $_" }
	}
}
