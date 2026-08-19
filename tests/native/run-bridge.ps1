# =============================================================================
#  End-to-end bridge test:  stock RPR 1.0 (HLA 1.3)  ->  VR-Exchange  ->  stock RPR 2.0 (Evolved)
#
#      Federation A   RTI 4.5.1(vc14)   4000 / 229.7.7.7    assistant 6003   rprHla13Broker
#      Federation B   RTI 5.0.1         5000 / 229.7.7.10   assistant 6004   rprHla1516eBroker
#
#  This is the whole point of the kit in one script: bytes go in on federation A, the broker
#  translates, and what federation B receives is asserted against what was sent.
#
#  Two things have to be right or nothing crosses, and neither one produces an error:
#
#    1. RTI_CONFIG for the federation-B agent points at the VR-EXCHANGE bin64 directory, not
#       at repo\config. The 1516e broker joins with RPR_FOM_v2.0_1516-2010.xml PLUS the 16
#       FOM modules listed in bin64\MAK-1516e-Config.xml. Our subscriber has to bring the
#       same 16 (rpr-sub-fedB.txt passes them as joinfom=), and they have to resolve to the
#       broker's own copies. Join with the bare FDD and the subscriber sees nothing at all.
#
#    2. The subscription has to be on GroundVehicle. The broker registers the MAK-extended
#       class MAK_GroundVehicle; class promotion reports it here as GroundVehicle. Subscribe
#       to an ancestor and the object is discovered as that ancestor with only the ancestor's
#       attributes, which looks like a half-broken bridge but is not.
#
#  Order matters: the portal's brokers must be joined before the agents publish, and the
#  subscriber must be listening before the publisher updates, or the update is simply missed.
# =============================================================================
param(
    [int]$SettleSeconds = 12,
    [string]$VrxBin     = "C:\MAK\vrexchange2.9\bin64"
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$out  = Join-Path $env:TEMP "btk-bridge-run"
New-Item -ItemType Directory -Force -Path $out | Out-Null

function Kill-All {
    Get-Process vrx,vrxd,rprHla13Broker,rprHla1516eBroker,rtiAssistant,btk-agent13,btk-agent1516e `
        -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

Kill-All
Start-Sleep -Seconds 2

# Broker logs APPEND across runs. Left alone, a "Joined federation" line from an earlier
# run reads as success this time - which is exactly the sort of stale evidence that sends
# a diagnosis in the wrong direction. Truncate first so every line below is from this run.
foreach ($f in @("BTK-FedA.log", "BTK-FedB.log")) {
    $q = Join-Path $VrxBin $f
    if (Test-Path $q) { Clear-Content -LiteralPath $q -ErrorAction SilentlyContinue }
}

# ---- 1. the bridge --------------------------------------------------------
Write-Host "[1/4] starting VR-Exchange (both brokers)..."
$portal = Start-Process -FilePath "cmd.exe" -WorkingDirectory $VrxBin -PassThru -WindowStyle Hidden `
    -ArgumentList "/c `"`"$VrxBin\vrx.exe`" -l BTK-Bridge.xml -G > `"$out\portal.out`" 2>&1 < NUL`""
Start-Sleep -Seconds $SettleSeconds

$joined = @(Select-String -Path "$VrxBin\BTK-FedA.log","$VrxBin\BTK-FedB.log" -Pattern "Joined federation" -ErrorAction SilentlyContinue)
Write-Host ("      brokers joined: " + $joined.Count + " of 2")
if ($joined.Count -lt 2) {
    Write-Host "      portal output (config errors appear ONLY here, never in the log file):"
    Get-Content "$out\portal.out" -ErrorAction SilentlyContinue | Select-Object -First 12 | ForEach-Object { "        $_" }
}

# ---- 2. subscriber on federation B ---------------------------------------
Write-Host "[2/4] subscriber joining federation B (RPR 2.0, RTI 5.0.1)..."
$sub = Start-Job -ScriptBlock {
    param($repo, $vrxBin, $script, $log)
    $env:RTI_CONFIG            = $vrxBin        # see header note 1 - resolves fed= and joinfom=
    $env:RTI_RID_FILE          = "$repo\config\rid-fedB-rti501.mtl"
    $env:RTI_ASSISTANT_PORT    = "6004"
    $env:MAKLMGRD_LICENSE_FILE = "27000@127.0.0.1"
    $env:PATH                  = "C:\MAK\makRti5.0.1\bin;$env:PATH"
    & cmd /c "`"$repo\build\native\btk-agent1516e.exe`" < `"$script`" > `"$log`" 2>&1"
} -ArgumentList $repo, $VrxBin, "$PSScriptRoot\rpr-sub-fedB.txt", "$out\sub-fedB.out"
Start-Sleep -Seconds 10

# ---- 3. publisher on federation A ----------------------------------------
Write-Host "[3/4] publisher on federation A (RPR 1.0, RTI 4.5.1 vc14)..."
$env:RTI_CONFIG            = "$repo\config"
$env:RTI_RID_FILE          = "$repo\config\rid-fedA-rti451.mtl"
$env:RTI_ASSISTANT_PORT    = "6003"
$env:MAKLMGRD_LICENSE_FILE = "27000@127.0.0.1"
$env:PATH                  = "C:\MAK\makRti4.5.1(vc14)\bin;$env:PATH"
$pub = Start-Process -FilePath "cmd.exe" -PassThru -WindowStyle Hidden `
    -ArgumentList "/c `"`"$repo\build\native\btk-agent13.exe`" < `"$PSScriptRoot\rpr-pub-fedA.txt`" > `"$out\pub-fedA.out`" 2>&1`""
if (-not $pub.WaitForExit(60000)) { $pub.Kill() }

# ---- 4. results -----------------------------------------------------------
Write-Host "[4/4] collecting..."
$sub | Wait-Job -Timeout 45 | Out-Null
$sub | Remove-Job -Force -ErrorAction SilentlyContinue
Kill-All

Write-Host ""
Write-Host "===== FEDERATION A - what was published ====="
Get-Content "$out\pub-fedA.out" -ErrorAction SilentlyContinue |
    Select-String "OK JOIN|OK CLASS|OK REGISTER|OK UPDATE|ERR" | ForEach-Object { "  " + $_.ToString().Trim() }

Write-Host ""
Write-Host "===== THE BROKER ====="
Select-String -Path "$VrxBin\BTK-FedA.log" -Pattern "Discovered Entity" -ErrorAction SilentlyContinue |
    ForEach-Object { "  A: " + $_.Line.Trim() }
Select-String -Path "$VrxBin\BTK-FedB.log" -Pattern "Registered object" -ErrorAction SilentlyContinue |
    ForEach-Object { "  B: " + $_.Line.Trim() }

Write-Host ""
Write-Host "===== FEDERATION B - what the BROKER delivered ====="
Get-Content "$out\sub-fedB.out" -ErrorAction SilentlyContinue |
    Select-String "OK JOIN|OK CLASS|DISC|ATTR |SNAPSHOT|ERR" | ForEach-Object { "  " + $_.ToString().Trim() }

# ---- the actual assertion -------------------------------------------------
# EntityIdentifier / EntityType / Marking encode identically in both FOMs, so these are
# byte-for-byte. Spatial is not: RPR 2.0 has no WorldLocation, so the expected value is the
# SpatialFPStruct the broker builds - discriminant, the same 24 WorldLocation bytes, then
# IsFrozen / Orientation / VelocityVector with alignment padding.
$expected = [ordered]@{
    EntityIdentifier = "000100010065"
    EntityType       = "010100e101010000"
    Marking          = "0142544b2d53544f434b2d31"
    Spatial          = "0200000000000000c14491b80000000040934a00000000004154fb180000000000000000000000000000000000000000000000000000000000000000"
}
$received = @{}
Get-Content "$out\sub-fedB.out" -ErrorAction SilentlyContinue |
    Select-String "^ATTR .* name=(\S+) len=\d+ hex=(\S+)" |
    ForEach-Object { $m = $_.Matches[0]; $received[$m.Groups[1].Value] = $m.Groups[2].Value }

Write-Host ""
Write-Host "===== VERDICT ====="
$fail = 0
foreach ($k in $expected.Keys) {
    if (-not $received.ContainsKey($k))          { Write-Host "  MISSING  $k"; $fail++ }
    elseif ($received[$k] -ne $expected[$k])     { Write-Host "  MISMATCH $k"; Write-Host "           sent     $($expected[$k])"; Write-Host "           received $($received[$k])"; $fail++ }
    else                                         { Write-Host "  OK       $k  $($received[$k])" }
}
if ($fail -eq 0) {
    Write-Host ""
    Write-Host "RESULT: PASS - RPR 1.0 (HLA 1.3) -> VR-Exchange -> RPR 2.0 (Evolved) verified."
    exit 0
} else {
    Write-Host ""
    Write-Host "RESULT: FAIL - $fail attribute(s) wrong. Transcripts in $out"
    exit 1
}
