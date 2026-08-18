@echo off
REM ============================================================================
REM  Build the BrokerTestKit native agents (raw MAK RTI, no VR-Link).
REM
REM  Why a .bat and not a .vcxproj: the solution's C# projects are AnyCPU/x64 and
REM  mixing a native platform into that .sln causes more grief than it saves. The
REM  agents are two self-contained translation units - this is the whole build.
REM
REM  Toolchain notes (learned the hard way):
REM    * /std:c++14 is REQUIRED. HLA 1.3 headers use dynamic exception
REM      specifications - throw(A,B) - which C++17 REMOVED. /std:c++17 will not compile.
REM    * /DRTI_USES_STD_FSTREAM is REQUIRED. Without it RTI.hh includes <fstream.h>,
REM      a pre-standard header that no modern MSVC ships.
REM    * VS2022 (VC17) is ABI-compatible with the VC14/VC15 RTI libraries - all of
REM      VC14..VC17 share the universal CRT. VC10 and earlier would NOT be.
REM ============================================================================
setlocal

set RTI_ROOT=C:\MAK\makRti5.0.1
set VCVARS=C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvars64.bat
set OUTDIR=%~dp0..\..\build\native

if not exist "%RTI_ROOT%\include\HLA13\RTI.hh" (
    echo [ERROR] MAK RTI headers not found under %RTI_ROOT%
    exit /b 1
)
if not exist "%VCVARS%" (
    echo [ERROR] vcvars64.bat not found: %VCVARS%
    exit /b 1
)

call "%VCVARS%" >nul
if errorlevel 1 (echo [ERROR] vcvars64 failed & exit /b 1)

if not exist "%OUTDIR%" mkdir "%OUTDIR%"
pushd "%~dp0"

echo [1/2] btk-agent13.exe  (HLA 1.3)
cl /nologo /EHsc /MD /std:c++14 /W3 /wd4290 /O2 ^
   /DRTI_USES_STD_FSTREAM /DWIN32 /D_CRT_SECURE_NO_WARNINGS ^
   /I "%RTI_ROOT%\include\HLA13" ^
   /Fo"%OUTDIR%\\" ^
   /Fe"%OUTDIR%\btk-agent13.exe" ^
   btk_agent13.cpp ^
   /link /LIBPATH:"%RTI_ROOT%\lib" libRTI-NG_64.lib libfedtime_64.lib
if errorlevel 1 (echo [ERROR] compile failed ^(agent13^) & popd & exit /b 1)

echo [2/2] btk-agent1516e.exe  (HLA Evolved / IEEE 1516-2010)
cl /nologo /EHsc /MD /std:c++14 /W3 /wd4290 /O2 ^
   /DRTI_USES_STD_FSTREAM /DWIN32 /D_CRT_SECURE_NO_WARNINGS ^
   /D_SILENCE_CXX17_AUTO_PTR_DEPRECATION_WARNING ^
   /I "%RTI_ROOT%\include\HLA1516E" ^
   /Fo"%OUTDIR%\\" ^
   /Fe"%OUTDIR%\btk-agent1516e.exe" ^
   btk_agent1516e.cpp ^
   /link /LIBPATH:"%RTI_ROOT%\lib" librti1516e64.lib libfedtime1516e64.lib
if errorlevel 1 (echo [ERROR] compile failed ^(agent1516e^) & popd & exit /b 1)

popd
echo.
echo [OK] built -^> %OUTDIR%
echo      run with: set PATH=%RTI_ROOT%\bin;%%PATH%%
endlocal
