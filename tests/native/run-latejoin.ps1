# =============================================================================
#  LATE-JOIN REPRO:  does a late join lose EntityType across VR-Exchange?
#
#  The CGF publishes every attribute once at registration and then only moves.
#  That is normal HLA. The question is what happens to a listener that was not
#  there for the one update that carried EntityType.
#
#  TWO listeners can be the late one. They fail differently and need different
#  fixes, so each scenario makes exactly ONE of them late and nobody else:
#
#    -Scenario BrokerLate       pub -> subscriber -> PORTAL LAST.
#                               VR-Exchange's federation-A broker misses the full
#                               update. It never learns the type, so its state
#                               repository has nothing to forward.
#                               Loss is UPSTREAM, inside VR-Exchange.
#                               Fix belongs on federation A (RTI 4.5.1).
#
#    -Scenario SubscriberLate   portal -> pub -> SUBSCRIBER LAST.
#                               The broker has the type and republished it on B;
#                               our federate simply was not there.
#                               Loss is DOWNSTREAM of the broker, and the federate
#                               that must answer a provide request is VR-Exchange.
#                               Fix belongs on federation B (RTI 5.0.1).
#
#    -Scenario Control          portal -> subscriber -> pub. Nobody late.
#                               MUST pass. Run it first: a red control makes every
#                               other result worthless.
#
#  -Isolate     use latejoin-pub-fedA-isolate.txt, whose deltas also re-send
#               EntityIdentifier and WorldLocation. BTK-FedA-RPR1-rti451.bcf
#               requires entityId AND entityType AND location before it publishes
#               an entity, so without this a suppressed entity cannot be blamed on
#               the missing TYPE - three things are missing, not one.
#                   faithful run -> "does this reproduce production?"
#                   isolated run -> "is EntityType specifically what is lost?"
#
#  THE PUBLISHER AS ATTRIBUTE OWNER (read before trusting a BrokerLate result):
#  On federation A the attribute owner IS our agent, so whether a late broker can
#  recover depends on whether the agent answers provideAttributeValueUpdate.
#  btk-agent13 now does: requests are queued in the callback and answered after
#  tick() returns, from the MERGED state of everything ever sent for the object
#  (not just the last delta). -NoProvide passes --no-provide to model a CGF that
#  ignores requests. Consequences:
#    * Default: BrokerLate measures recovery against a CGF that answers.
#    * -NoProvide: BrokerLate reproduces the worst case, a CGF that never answers.
#    * RTI_autoProvideSwitch works by making the RTI invoke
#      provideAttributeValueUpdate on the OWNING federate, so it is only testable in
#      BrokerLate with provide ON.
#  Neither agent ever CALLS requestAttributeValueUpdate itself, so the federation-B
#  result still assumes a passive consumer (see mak-rti-knowledge.md section 10).
#  In SubscriberLate the owner is VR-Exchange itself, and VR-Link does answer.
#
#  Every trap that run-bridge.ps1 documents still applies: RTI_CONFIG points at
#  the VR-Exchange bin64 for the federation-B agent, the subscriber brings the
#  same 16 FOM modules as the broker, and it subscribes at GroundVehicle.
# =============================================================================
param(
    [ValidateSet("Control", "BrokerLate", "SubscriberLate", "BothLate")]
    [string]$Scenario = "BrokerLate",
    [switch]$Isolate,
    [switch]$AutoProvide,
    # Who owns the switch? HLA defines the switch table as federation-wide, established at
    # CREATE from the FDD; MAK documents RTI_preferRidToFedSwitchTable under "LRC-specific
    # parameters", which hints per-federate. The docs never say which wins, so these two
    # split the pair and let the rig answer it.
    #   -AutoProvideCreatorOnly  the FedB BROKER (which creates federation B) has it; the
    #                            late-joining subscriber does not.
    #   -AutoProvideJoinerOnly   only the late joiner has it; the creator does not.
    [switch]$AutoProvideCreatorOnly,
    [switch]$AutoProvideJoinerOnly,
    # Operational workaround under test: once a consumer is stuck with a typeless entity,
    # does bouncing the BROKER recover it? Restarts the portal mid-run, with the publisher
    # and the consumer both left running.
    [switch]$Bounce,
    # VR-Exchange broker setting, not an RTI one: <heartbeat> in the FedB .bcf.
    # "The frequency with which to send an update if there is no change. Default: -1
    # (means do not heartbeat)." If a heartbeat re-sends the FULL attribute set, a late
    # joiner recovers within one interval with no RTI switch and no consumer change.
    [switch]$Heartbeat,
    # 100 entities: t1-t50 move every tick, t51-t100 register once then idle. Measures
    # how heartbeat traffic scales, and whether a real update resets the heartbeat timer.
    [switch]$Volume,
    # The STANDARD route: autoProvide set in the FOM the CREATING federate loads, with
    # every rid left at preferRidToFedSwitchTable 0. Federation-wide, portable, and the
    # FOM keeps describing actual behaviour. The subscriber deliberately keeps its
    # UNMODIFIED FOM copy, so a pass proves the creator's table governs everyone.
    [switch]$FomAutoProvide,
    # as -FomAutoProvide, but the SUBSCRIBER also loads the autoProvide FOM copy
    [switch]$FomAutoProvideBoth,
    # The .bcf <heartbeat> unit is NOT documented as ticks, but 5 produced ~194 updates/sec
    # against pollingInterval=1(ms). This selects an alternate value to pin the unit down.
    [int]$HeartbeatTicks = 0,
    # Model a CGF that ignores provideAttributeValueUpdate. btk-agent13 now answers by
    # default, which is what HLA requires of a federate; this restores the old silence so
    # the two behaviours can be compared.
    [switch]$NoProvide,
    [int]   $Timeout  = 60,
    [string]$VrxBin   = "C:\MAK\vrexchange2.9\bin64",
    # SAME RTI on both sides by default: MAK RTI 5.0.1 ships libRTI-NG_64.dll (HLA 1.3)
    # AND librti1516e64.dll (Evolved), so one product serves both federations. They stay
    # separate by federation name and by connection (4000/229.7.7.7 vs 5000/229.7.7.10),
    # never by RTI version.
    # -SplitRti restores the older topology with federation A on 4.5.1(vc14).
    [switch]$SplitRti
)

$ErrorActionPreference = "Stop"
$repo    = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tag     = $Scenario + $(if ($Isolate) { "-isolate" }) + $(if ($AutoProvide) { "-autoprovide" }) + $(if ($NoProvide) { "-noprovide" })
$out     = Join-Path $env:TEMP "btk-latejoin-$tag"
$pubFile = if ($Volume) { "latejoin-pub-fedA-vol.txt" } elseif ($Isolate) { "latejoin-pub-fedA-isolate.txt" } else { "latejoin-pub-fedA.txt" }

# -AutoProvide swaps BOTH the broker's RID (via its own bridge config) and the
# harness agent's, because HLAautoProvide is federation-wide and set at create
# time by whoever creates the execution. One RID file backs both.
#
# Federation A needs it too, for BrokerLate. And on federation A the federation is
# created by the PUBLISHER (it starts before the portal in that scenario), so the
# publisher gets the variant RID as well as the broker.
# The RTI bin directories are PARAMETERS, not literals. They were hard-coded, which meant
# -VrxBin retargeted the portal while silently leaving the agents on the original RTIs -
# and the prerequisite check still passed, because the literals existed either way.
if ($SplitRti) {
    $rtiBinA   = "C:\MAK\makRti4.5.1(vc14)\bin"
    $pubRid    = if ($AutoProvide) { "rid-fedA-rti451-autoprovide.mtl" } else { "rid-fedA-rti451.mtl" }
    $bridgeXml = if ($AutoProvide) { "BTK-Bridge-autoprovide.xml" }      else { "BTK-Bridge.xml" }
} else {
    $rtiBinA   = "C:\MAK\makRti5.0.1\bin"
    $pubRid    = if ($AutoProvide) { "rid-fedA-rti501-autoprovide.mtl" } else { "rid-fedA-rti501.mtl" }
    $bridgeXml = if ($AutoProvide) { "BTK-Bridge-same501-autoprovide.xml" } else { "BTK-Bridge-same501.xml" }
}
$rtiBinB = "C:\MAK\makRti5.0.1\bin"
$subRid  = if ($AutoProvide) { "rid-fedB-rti501-autoprovide.mtl" } else { "rid-fedB-rti501.mtl" }

# Split the pair. The bridge XML carries the BROKER's rid (the creator of federation B);
# $subRid carries the late JOINER's. Setting exactly one isolates whose copy governs.
if ($AutoProvideCreatorOnly) {
    $bridgeXml = "BTK-Bridge-same501-autoprovide.xml"   # creator: switch ON
    $subRid    = "rid-fedB-rti501.mtl"                  # joiner:  switch OFF
    $tag       = "$Scenario-creatoronly"
    $out       = Join-Path $env:TEMP "btk-latejoin-$tag"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
}
if ($AutoProvideJoinerOnly) {
    $bridgeXml = "BTK-Bridge-same501.xml"               # creator: switch OFF
    $subRid    = "rid-fedB-rti501-autoprovide.mtl"      # joiner:  switch ON
    $tag       = "$Scenario-joineronly"
    $out       = Join-Path $env:TEMP "btk-latejoin-$tag"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
}
# Last, so it wins over the variants above. -Heartbeat is a VR-Exchange BROKER setting
# (<heartbeat> in the FedB .bcf), not an RTI one, so it does not compose with the
# autoProvide variants - pick one or the other.
if ($FomAutoProvide -or $FomAutoProvideBoth) {
    $bridgeXml = "BTK-Bridge-same501-fomap.xml"
    $tag       = "$Scenario-fomap"
    $out       = Join-Path $env:TEMP "btk-latejoin-$tag"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
}
if ($Heartbeat) {
    $bridgeXml = if ($HeartbeatTicks -gt 0) { "BTK-Bridge-same501-hb$HeartbeatTicks.xml" } else { "BTK-Bridge-same501-heartbeat.xml" }
    $tag       = "$Scenario-heartbeat"
    $out       = Join-Path $env:TEMP "btk-latejoin-$tag"
    New-Item -ItemType Directory -Force -Path $out | Out-Null
}
# Guard: every selector above is a silent no-op if the file is absent.
if (-not (Test-Path -LiteralPath "$VrxBin\$bridgeXml")) { throw "bridge config not found: $VrxBin\$bridgeXml" }

New-Item -ItemType Directory -Force -Path $out | Out-Null

$pubLog = "$out\pub-fedA.out"
$subLog = "$out\sub-fedB.out"
$fedALog = "$VrxBin\BTK-FedA.log"
$fedBLog = "$VrxBin\BTK-FedB.log"

foreach ($p in @("$repo\build\native\btk-agent13.exe",
                 "$repo\build\native\btk-agent1516e.exe",
                 "$PSScriptRoot\$pubFile",
                 "$PSScriptRoot\latejoin-sub-fedB.txt",
                 "$VrxBin\vrx.exe",
                 "$VrxBin\$bridgeXml",
                 "$repo\config\$subRid",
                 $rtiBinA,
                 $rtiBinB)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "missing prerequisite: $p" }
}

function Kill-All {
    Get-Process vrx,vrxd,rprHla13Broker,rprHla1516eBroker,rtiAssistant,btk-agent13,btk-agent1516e `
        -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
}

function Abort($msg) {
    Write-Host ""
    Write-Host "ABORTED - $msg"
    Write-Host "  Transcripts: $out"
    Write-Host "  A verdict from a run that did not reach its starting line would be a guess."
    Kill-All
    exit 2
}

# Every ordering below waits for the EVENT, not for a guessed number of seconds.
# A fixed sleep that is a second too short turns BrokerLate into Control silently.
function Grep-Count {
    # Select-String on a not-yet-created log raises PathNotFound, and with
    # $ErrorActionPreference='Stop' that kills the run even with
    # -ErrorAction SilentlyContinue. Waiting for a file that does not exist yet is
    # the NORMAL case here, so filter to existing paths first.
    param([string[]]$Path, [string]$Pattern)
    $live = @($Path | Where-Object { Test-Path -LiteralPath $_ })
    if (-not $live.Count) { return @() }
    return @(Select-String -LiteralPath $live -Pattern $Pattern -ErrorAction SilentlyContinue)
}

function Wait-For {
    param([string[]]$Path, [string]$Pattern, [int]$Need = 1, [int]$TimeoutSec = 60, [string]$What = "event")
    $sw = [Diagnostics.Stopwatch]::StartNew()
    while ($sw.Elapsed.TotalSeconds -lt $TimeoutSec) {
        if ((Grep-Count -Path $Path -Pattern $Pattern).Count -ge $Need) {
            Write-Host ("      + {0} after {1:N1}s" -f $What, $sw.Elapsed.TotalSeconds)
            return $true
        }
        Start-Sleep -Milliseconds 500
    }
    Write-Host ("      ! TIMED OUT waiting for {0} after {1}s" -f $What, $TimeoutSec)
    return $false
}

$PubBlock = {
    param($repo, $script, $log, $rid, $noProvide, $rtiBin)
    $env:RTI_CONFIG            = "$repo\config"
    $env:RTI_RID_FILE          = "$repo\config\$rid"
    $env:RTI_ASSISTANT_PORT    = "6003"
    $env:MAKLMGRD_LICENSE_FILE = "27000@127.0.0.1"
    $env:PATH                  = "$rtiBin;$env:PATH"
    $flag = if ($noProvide) { " --no-provide" } else { "" }
    & cmd /c "`"$repo\build\native\btk-agent13.exe`"$flag < `"$script`" > `"$log`" 2>&1"
}

$SubBlock = {
    param($repo, $vrxBin, $script, $log, $rid, $rtiBin)
    $env:RTI_CONFIG            = $vrxBin          # resolves fed= and the 16 joinfom= modules
    $env:RTI_RID_FILE          = "$repo\config\$rid"
    $env:RTI_ASSISTANT_PORT    = "6004"
    $env:MAKLMGRD_LICENSE_FILE = "27000@127.0.0.1"
    $env:PATH                  = "$rtiBin;$env:PATH"
    & cmd /c "`"$repo\build\native\btk-agent1516e.exe`" < `"$script`" > `"$log`" 2>&1"
}

function Start-Pub { Start-Job -ScriptBlock $PubBlock -ArgumentList $repo, "$PSScriptRoot\$pubFile", $pubLog, $pubRid, ([bool]$NoProvide), $rtiBinA }
$subFile = if ($FomAutoProvideBoth) { "latejoin-sub-fedB-ap.txt" } else { "latejoin-sub-fedB.txt" }
function Start-Sub { Start-Job -ScriptBlock $SubBlock -ArgumentList $repo, $VrxBin, "$PSScriptRoot\$subFile", $subLog, $subRid, $rtiBinB }
function Start-Portal {
    Start-Process -FilePath "cmd.exe" -WorkingDirectory $VrxBin -PassThru -WindowStyle Hidden `
        -ArgumentList "/c `"`"$VrxBin\vrx.exe`" -l $bridgeXml -G > `"$out\portal.out`" 2>&1 < NUL`""
}

# Each of these ABORTS rather than continuing, because every one of them
# otherwise produces silence on federation B that is indistinguishable from
# the bug we are trying to measure.
function Require-Brokers {
    if (-not (Wait-For -Path @($fedALog, $fedBLog) -Pattern "Joined federation" -Need 2 -TimeoutSec $Timeout -What "both brokers joined")) {
        Write-Host "      portal stdout (config errors appear ONLY here, never in the log file):"
        Get-Content "$out\portal.out" -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object { "        $_" }
        Abort "VR-Exchange did not get both brokers into their federations."
    }
}
function Require-PubFullUpdate {
    if (-not (Wait-For -Path $pubLog -Pattern "OK UPDATE .*attrs=8" -TimeoutSec $Timeout -What "publisher's full update (attrs=8)")) {
        Get-Content $pubLog -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object { "        $_" }
        Abort "the publisher never sent the full update the whole test is built on."
    }
}
function Require-SubJoined {
    if (-not (Wait-For -Path $subLog -Pattern "OK JOIN" -TimeoutSec $Timeout -What "subscriber joined federation B")) {
        Get-Content $subLog -ErrorAction SilentlyContinue | Select-Object -First 15 | ForEach-Object { "        $_" }
        Abort "our federate never joined federation B."
    }
}
function Require-BrokerRepublished {
    if (-not (Wait-For -Path $fedBLog -Pattern "Registered object" -TimeoutSec $Timeout -What "broker republished the entity on federation B")) {
        Abort "the broker never registered the entity on federation B, so there is nothing for a late subscriber to miss."
    }
}

Kill-All
Start-Sleep -Seconds 2

# Broker logs APPEND. A "Joined federation" line from an earlier run reads as success
# this time - exactly the stale evidence that sends a diagnosis the wrong way.
foreach ($f in @($fedALog, $fedBLog)) {
    if (Test-Path $f) { Clear-Content -LiteralPath $f -ErrorAction SilentlyContinue }
}
foreach ($f in @($pubLog, $subLog, "$out\portal.out")) {
    if (Test-Path $f) { Remove-Item -LiteralPath $f -Force -ErrorAction SilentlyContinue }
}

Write-Host "=============================================================="
Write-Host " scenario : $Scenario$(if ($Isolate) { '  (isolated: deltas re-send id + location)' })"
Write-Host " publisher: $pubFile"
Write-Host " fed A rti: $rtiBinA"
Write-Host " fed B rti: $rtiBinB"
Write-Host " fed A rid: $pubRid$(if ($NoProvide) { "   agent13 --no-provide" })"
Write-Host " fed B rid: $subRid"
Write-Host " bridge   : $bridgeXml$(if ($Heartbeat) { '   <-- broker heartbeat 5s' })$(if ($AutoProvide) { '   <-- HLAautoProvide ON' })"
Write-Host "=============================================================="

switch ($Scenario) {

    "Control" {
        Write-Host "[1/3] VR-Exchange first..."
        $portal = Start-Portal ; Require-Brokers
        Write-Host "[2/3] subscriber joins federation B..."
        $sub = Start-Sub       ; Require-SubJoined
        Write-Host "[3/3] publisher - everyone was already listening"
        $pub = Start-Pub       ; Require-PubFullUpdate
    }

    "BrokerLate" {
        # Subscriber goes up BEFORE the portal. Otherwise it is late too and the
        # scenario confounds the two variables it exists to separate.
        Write-Host "[1/3] publisher FIRST - registers and sends its one full update now"
        $pub = Start-Pub       ; Require-PubFullUpdate
        Write-Host "[2/3] subscriber joins federation B early, so ONLY the broker is late"
        $sub = Start-Sub       ; Require-SubJoined
        Write-Host "[3/3] NOW starting VR-Exchange - the broker has already missed the full update"
        $portal = Start-Portal ; Require-Brokers
    }

    "BothLate" {
        # The real-world ordering: the CGF is already running, then the bridge is brought
        # up, then a consumer connects. BOTH late joins stack -
        #   upstream   the broker missed the CGF's full update
        #   downstream the consumer missed the broker's republication on B
        # They are not independent: if the CGF answers the broker's request, the broker
        # recovers and registers on B - and the consumer is then late for THAT.
        Write-Host "[1/3] publisher - the CGF is already running and has sent its full update"
        $pub = Start-Pub       ; Require-PubFullUpdate
        Write-Host "[2/3] NOW starting VR-Exchange - both brokers join late"
        $portal = Start-Portal ; Require-Brokers
        Write-Host "[3/3] consumer joins federation B last of all"
        $sub = Start-Sub       ; Require-SubJoined
    }

    "SubscriberLate" {
        Write-Host "[1/3] VR-Exchange first..."
        $portal = Start-Portal ; Require-Brokers
        Write-Host "[2/3] publisher - the broker receives the full update and republishes on B"
        $pub = Start-Pub       ; Require-PubFullUpdate ; Require-BrokerRepublished
        Write-Host "[3/3] NOW joining federation B - our federate has already missed it"
        $sub = Start-Sub       ; Require-SubJoined
    }
}

if ($Bounce) {
    Write-Host ""
    Write-Host "[bounce] letting the consumer settle into the broken state first..."
    Start-Sleep -Seconds 20
    Write-Host "[bounce] stopping VR-Exchange (publisher and consumer stay up)"
    # Stop the PORTAL ONLY and let the brokers notice. Killing the brokers outright is a
    # crash, not a resign: their objects on federation B are never deleted, the consumer
    # never gets removeObjectInstance, and it is left holding a frozen ghost. Losing the
    # portal's TCP connection makes each broker shut down of its own accord
    # ("Lost TCP Connection to Queue Manager" -> deletes translators -> resigns), which is
    # what an operator disabling a connection would actually do.
    Get-Process vrx,vrxd -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    $gone = $false
    for ($i = 0; $i -lt 30; $i++) {
        Start-Sleep -Milliseconds 500
        if (-not (Get-Process rprHla13Broker,rprHla1516eBroker -ErrorAction SilentlyContinue)) { $gone = $true; break }
    }
    if ($gone) { Write-Host "      + brokers resigned and exited on their own" }
    else {
        Write-Host "      ! brokers did not exit within 15s - forcing (this is a CRASH, not a resign)"
        Get-Process rprHla13Broker,rprHla1516eBroker -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
    Start-Sleep -Seconds 3
    # Broker logs APPEND, so the pre-bounce "Joined federation" lines would satisfy
    # Require-Brokers instantly and we would not actually wait for the restart.
    foreach ($f in @($fedALog, $fedBLog)) {
        if (Test-Path $f) { Clear-Content -LiteralPath $f -ErrorAction SilentlyContinue }
    }
    Write-Host "[bounce] restarting VR-Exchange"
    $portal = Start-Portal ; Require-Brokers
}

Write-Host ""
Write-Host "running to completion (~90 s)..."
$pub | Wait-Job -Timeout 240 | Out-Null
$sub | Wait-Job -Timeout 240 | Out-Null
$pub, $sub | Remove-Job -Force -ErrorAction SilentlyContinue
Kill-All

# ---- evidence --------------------------------------------------------------
$pubOut = @(Get-Content $pubLog -ErrorAction SilentlyContinue)
$subOut = @(Get-Content $subLog -ErrorAction SilentlyContinue)

$fullUpd = @($pubOut | Select-String "OK UPDATE .*attrs=8").Count
$dltUpd  = @($pubOut | Select-String "OK UPDATE .*attrs=[12]").Count
$discA   = Grep-Count -Path $fedALog -Pattern "Discovered Entity"
$regB    = Grep-Count -Path $fedBLog -Pattern "Registered object"

Write-Host ""
Write-Host "===== FEDERATION A - what the CGF published ====="
$pubOut | Select-String "OK JOIN|OK REGISTER|ERR" | Select-Object -First 6 | ForEach-Object { "  " + $_.ToString().Trim() }
Write-Host "  full updates (attrs=8): $fullUpd     delta updates: $dltUpd"

# The decisive evidence for the UPSTREAM path, and it is invisible unless printed here:
# VR-Exchange's HLA 1.3 broker asks the owner for a full update when it discovers an
# object it has no values for. Whether the type crosses then depends entirely on whether
# the owner answers.
$reqd = @($pubOut | Select-String "^PROVIDEREQ ").Count
$ansd = @($pubOut | Select-String "^OK PROVIDE ").Count
Write-Host "  provide requests FROM the broker: $reqd     answered by the CGF: $ansd"
if ($reqd -gt 0 -and $ansd -eq 0) {
    Write-Host "    -> the broker asked and got silence. That is the upstream failure mode."
}

Write-Host ""
Write-Host "===== THE BROKER ====="
if ($discA.Count) { $discA | Select-Object -First 2 | ForEach-Object { "  A: " + $_.Line.Trim() } }
else              { Write-Host "  A: (never discovered the entity on federation A)" }
if ($regB.Count)  { $regB  | Select-Object -First 2 | ForEach-Object { "  B: " + $_.Line.Trim() } }
else              { Write-Host "  B: (never registered anything on federation B)" }

Write-Host ""
Write-Host "===== FEDERATION B - what our federate saw ====="
$subOut | Select-String "^(OK JOIN|DISC |ATTR |OK SNAPSHOT|ERR)" | ForEach-Object { "  " + $_.ToString().Trim() }

# Anchored. An unanchored, case-insensitive "DISC" also matches RTI banner text
# such as "Disconnected" or "Loading Config File", which would turn a dead
# subscriber into an apparent downstream loss.
$seen = @{}
$subOut | Select-String "^ATTR .* name=(\S+) len=\d+ hex=(\S+)" |
    ForEach-Object { $m = $_.Matches[0]; $seen[$m.Groups[1].Value] = $m.Groups[2].Value }
$discovered = @($subOut | Select-String "^DISC ").Count -gt 0

# COVERAGE, not existence. $hasType used to mean "some object somewhere has a type",
# which on a 100-entity run reported a PASS when only 11 of 100 recovered. Any verdict
# that ignores the other 89 is worthless.
$objAll  = @($subOut | Select-String "^DISC "                      | ForEach-Object { ($_ -split "obj=")[1].Split(" ")[0] } | Sort-Object -Unique)
$objType = @($subOut | Select-String "^ATTR .* name=EntityType "   | ForEach-Object { ($_ -split "obj=")[1].Split(" ")[0] } | Sort-Object -Unique)
$covered = $objType.Count
$total   = $objAll.Count
$hasSpatial = $seen.ContainsKey("Spatial")

# Presence is not enough. The reported production symptom is a BLANK type, and an
# all-zero EntityTypeStruct would satisfy a simple ContainsKey test while being
# exactly the failure we are hunting.
$EXPECTED_TYPE = "010100e101010000"
if     (-not $seen.ContainsKey("EntityType")) { $typeState = "absent" }
elseif ($seen["EntityType"] -match '^0+$')    { $typeState = "blank" }
elseif ($seen["EntityType"] -eq $EXPECTED_TYPE) { $typeState = "correct" }
else                                          { $typeState = "unexpected" }

# When did it first appear - live, or only after N snapshots (i.e. healed late)?
$firstType = ($subOut | Select-String "^ATTR .* name=EntityType " | Select-Object -First 1)
$snapsBefore = 0
if ($firstType -and $subOut.Count -gt 0) {
    $upto = [Math]::Max(0, [Math]::Min($firstType.LineNumber - 1, $subOut.Count - 1))
    $snapsBefore = @($subOut[0..$upto] | Select-String "^OK SNAPSHOT").Count
}

Write-Host ""
Write-Host "===== VERDICT ====="
Write-Host ("  object discovered on B : " + $(if ($discovered) { "yes" } else { "NO" }))
Write-Host ("  Spatial (position)     : " + $(if ($hasSpatial) { "yes - the bridge is alive" } else { "NO" }))
Write-Host ("  EntityType             : $typeState" + $(if ($seen.ContainsKey("EntityType")) { "  " + $seen["EntityType"] } else { "" }))
Write-Host ("  objects with EntityType: $covered of $total")
if ($total -gt 0 -and $covered -gt 0 -and $covered -lt $total) {
    Write-Host ""
    Write-Host "PARTIAL - $covered of $total objects got a type. A fix that reaches some objects and"
    Write-Host "  not others is not a fix; treat this as REPRODUCED and find out what the covered"
    Write-Host "  ones had in common (usually: they were registered AFTER the consumer subscribed)."
    exit 3
}
Write-Host ""

if ($fullUpd -lt 1) { Abort "no full update in the transcript." }

if ($Scenario -eq "Control") {
    if ($typeState -eq "correct" -and $hasSpatial) {
        Write-Host "CONTROL PASS - type and position both cross when nobody joins late."
        Write-Host "  The rig is sound, so a late-join failure is a real finding."
        exit 0
    }
    Write-Host "CONTROL FAIL - the rig itself is broken. Fix this before trusting any late-join run."
    exit 1
}

if (-not $discovered) {
    Write-Host "ENTITY NEVER REACHED FEDERATION B."
    if (-not $regB.Count) {
        Write-Host "  The broker never registered it, so the loss is UPSTREAM, inside VR-Exchange."
        if ($Isolate) {
            Write-Host "  This was an ISOLATED run: identity and location were re-sent in every delta,"
            Write-Host "  so EntityType was the only publication criterion the broker could not meet."
            Write-Host "  That is direct evidence the missing TYPE is what suppressed the entity."
        } else {
            Write-Host "  This was a FAITHFUL run: the broker is missing EntityIdentifier, EntityType AND"
            Write-Host "  WorldLocation, and the .bcf requires all three. Re-run with -Isolate before"
            Write-Host "  attributing this to the type specifically."
        }
    } else {
        Write-Host "  The broker DID register on B, so our federate simply never saw it -"
        Write-Host "  suspect the subscription or the FOM module set, not the late join."
    }
    exit 3
}

if (-not $hasSpatial) {
    Write-Host "INCONCLUSIVE - an object was discovered but no position ever arrived."
    Write-Host "  Nothing is flowing, so 'no EntityType' says nothing about late join. This is the"
    Write-Host "  signature of the documented traps: wrong FOM module set, or a subscription one"
    Write-Host "  class too high. See the run-bridge.ps1 header."
    exit 2
}

if ($typeState -eq "absent" -or $typeState -eq "blank") {
    Write-Host "REPRODUCED - position keeps arriving, EntityType is $typeState."
    Write-Host "  The bridge is demonstrably alive, so this is a real attribute loss."
    if ($Scenario -eq "SubscriberLate") {
        Write-Host "  Loss is DOWNSTREAM of the broker. The owner on federation B is VR-Exchange,"
        Write-Host "  which DOES answer provide requests - so RTI_autoProvideSwitch is worth testing"
        Write-Host "  here, on the 5.0.1 side. Note its FDD currently forces autoProvide off."
    } else {
        Write-Host "  Loss is UPSTREAM, inside the broker's state repository."
        Write-Host "  CAVEAT: btk-agent13 cannot answer provideAttributeValueUpdate, so this result"
        Write-Host "  does NOT prove a real CGF behaves the same, and auto-provide cannot be"
        Write-Host "  evaluated in this scenario. See the header."
    }
    exit 3
}

Write-Host "NOT REPRODUCED - EntityType arrived correctly despite the late join."
if ($snapsBefore -gt 0) {
    Write-Host "  It appeared only after $snapsBefore snapshot(s), so something HEALED it rather than"
    Write-Host "  it never having been lost: auto-provide, a heartbeat, or a requested update."
} else {
    Write-Host "  It was there from the first reflection - this ordering does not lose the type."
}
exit 0
