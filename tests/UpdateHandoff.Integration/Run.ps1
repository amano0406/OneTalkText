$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($PSScriptRoot)
$run = Join-Path ([IO.Path]::GetTempPath()) ('OneTalkText-test-' + [Guid]::NewGuid().ToString('N'))
$oldOutput = Join-Path $run 'old-published'
$newOutput = Join-Path $run 'new-published'
foreach ($fixture in @('Old', 'New')) {
    $output = if ($fixture -eq 'Old') { $oldOutput } else { $newOutput }
    dotnet publish (Join-Path $root "$fixture\$fixture.csproj") -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o $output
    if ($LASTEXITCODE -ne 0) { throw "$fixture fixture failed to publish." }
}
if (!(Test-Path -LiteralPath $run)) { New-Item -ItemType Directory -Path $run | Out-Null }
$oldSource = Join-Path $oldOutput 'OneTalkText.exe'
$newSource = Join-Path $newOutput 'OneTalkText.exe'
$oldHash = (Get-FileHash -LiteralPath $oldSource -Algorithm SHA256).Hash
$newHash = (Get-FileHash -LiteralPath $newSource -Algorithm SHA256).Hash
if ($oldHash -eq $newHash) { throw 'The update fixtures must have different binaries.' }

foreach ($case in @('success', 'startup-failure', 'corrupt-download')) {
    $folder = Join-Path $run $case
    $working = Join-Path $folder 'working'
    $recordings = Join-Path $folder 'recordings'
    New-Item -ItemType Directory -Path $working, $recordings | Out-Null
    $target = Join-Path $folder 'OneTalkText.exe'
    Copy-Item -LiteralPath $oldSource -Destination $target
    $id = [Guid]::NewGuid().ToString('N')
    $candidate = Join-Path $working ('onetalk-update-' + $id + '.download')
    Copy-Item -LiteralPath $newSource -Destination $candidate
    $config = Join-Path $folder 'config.ini'
    $audio = Join-Path $recordings 'synthetic.wav'
    [IO.File]::WriteAllText($config, 'synthetic test settings')
    [IO.File]::WriteAllText($audio, 'synthetic test recording')
    $configHash = (Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash
    $audioHash = (Get-FileHash -LiteralPath $audio -Algorithm SHA256).Hash
    $digest = if ($case -eq 'corrupt-download') { '0' * 64 } else { $newHash.ToLowerInvariant() }
    $env:ONETALK_FIXTURE_FAIL = if ($case -eq 'startup-failure') { '1' } else { '0' }
    $parent = Start-Process -FilePath $target -ArgumentList @('--fixture-start', $id, $digest, (Get-Item -LiteralPath $candidate).Length.ToString()) -WindowStyle Hidden -PassThru
    if (!$parent.WaitForExit(30000)) { throw "$case`: fixture did not exit." }
    if ($parent.ExitCode -ne 0) { throw "$case`: handoff failed with exit code $($parent.ExitCode)." }
    $marker = Join-Path $folder $(if ($case -eq 'success') { 'new-started.ok' } else { 'old-restored.ok' })
    $deadline = [DateTime]::UtcNow.AddSeconds(40)
    while (!(Test-Path -LiteralPath $marker) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 200 }
    if (!(Test-Path -LiteralPath $marker)) {
        $log = Join-Path $folder 'fixture-log.txt'
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log }
        throw "$case`: expected startup marker was not produced."
    }
    $expected = if ($case -eq 'success') { $newHash } else { $oldHash }
    if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $expected) { throw "$case`: incorrect executable after update." }
    if ((Get-FileHash -LiteralPath $config -Algorithm SHA256).Hash -ne $configHash -or (Get-FileHash -LiteralPath $audio -Algorithm SHA256).Hash -ne $audioHash) {
        throw "$case`: settings or recording changed."
    }
    Write-Output "$case`: executable state, settings and recording verified."
}
