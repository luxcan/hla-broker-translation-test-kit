@echo off
REM ============================================================================
REM  REQ-08 proof, BOTH sides of the bridge: publish and receive CUSTOM
REM  (unmodelled) attributes and assert the bytes survive unchanged.
REM
REM    Federation A  HLA 1.3      customised .fed        btk-agent13.exe
REM    Federation B  HLA Evolved  RPR 2.0 + FOM module   btk-agent1516e.exe
REM
REM  This is the test the C# / VR-Link agent could not pass: it had no encoder for
REM  attributes outside the RPR FOM mapper, so BtkCustomValue / BtkCustomTag never
REM  reached the wire. The native agents put exact bytes on and read exact bytes off.
REM
REM  Run from anywhere:  tests\native\run-smoke.bat
REM ============================================================================
setlocal EnableDelayedExpansion
set REPO=%~dp0..\..
set OUT=%TEMP%\btk-native-smoke

if not exist "%REPO%\build\native\btk-agent13.exe" goto :nobuild
if not exist "%REPO%\build\native\btk-agent1516e.exe" goto :nobuild

REM MAK RTI environment. RTI_CONFIG resolves FOM files by name; RTI_RID_FILE must be OUR rid
REM (MAK's default enables rtiexec and the run then fails looking for a Forwarder).
set PATH=C:\MAK\makRti5.0.1\bin;%PATH%
set RTI_CONFIG=%REPO%\config
set RTI_RID_FILE=%REPO%\config\rid.mtl

if not exist "%OUT%" mkdir "%OUT%"
del /q "%OUT%\*.out" 2>nul
set FAIL=0

call :roundtrip 13     "HLA 1.3"
call :roundtrip 1516e  "HLA Evolved (1516-2010)"

echo.
if %FAIL%==0 (
    echo RESULT: PASS - custom attributes round-tripped byte-for-byte on BOTH federations.
) else (
    echo RESULT: FAIL - %FAIL% check^(s^) wrong. Transcripts in %OUT%
)
echo Transcripts: %OUT%
endlocal & exit /b %FAIL%

REM ---------------------------------------------------------------------------
:roundtrip
echo.
echo === %~2 ===
set AGENT=%REPO%\build\native\btk-agent%1.exe

echo   [1/3] subscriber joining...
start "btk-sub-%1" /b cmd /c ""%AGENT%" < "%~dp0sub%1.txt" > "%OUT%\sub%1.out" 2>"%OUT%\sub%1.err""

REM Let the subscriber join and subscribe before the publisher registers, otherwise the
REM discovery race makes the result non-deterministic.
ping -n 5 127.0.0.1 >nul

echo   [2/3] publisher sending 1 stock + 2 CUSTOM attributes...
"%AGENT%" < "%~dp0pub%1.txt" > "%OUT%\pub%1.out" 2>"%OUT%\pub%1.err"

REM Wait for the subscriber script (WAIT ms=9000) to finish and flush.
ping -n 8 127.0.0.1 >nul

echo   [3/3] asserting the bytes came back identical...
call :assert %1 "BtkCustomValue"   "name=BtkCustomValue len=8 hex=40934a0000000000"
call :assert %1 "BtkCustomTag"     "name=BtkCustomTag len=13 hex=435553544f4d2d5441472d3031"
call :assert %1 "EntityIdentifier" "name=EntityIdentifier len=6 hex=000100010065"
exit /b 0

REM ---------------------------------------------------------------------------
:assert
findstr /C:%3 "%OUT%\sub%1.out" >nul 2>&1
if errorlevel 1 (
    echo         FAIL  %~2
    set /a FAIL+=1
) else (
    echo         ok    %~2
)
exit /b 0

:nobuild
echo [ERROR] agents not built. Run: src\BrokerTestKit.AgentNative\build-native.bat
exit /b 1
