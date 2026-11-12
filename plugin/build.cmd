@echo off
rem Builds plugin\bin\ets2u_telemetry.dll (x64) with the Visual Studio C++ build tools.
rem Needs the SCS SDK unpacked in tools\scs_sdk.
setlocal
rem Set the path outside the for: the ")" in "(x86)" would end the for's parentheses.
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
if not defined VS (echo Visual Studio C++ build tools not found & exit /b 1)
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1

set ROOT=%~dp0..
if not exist "%~dp0bin" mkdir "%~dp0bin"
cl /nologo /O2 /EHsc /MT /LD /W4 /I"%ROOT%\tools\scs_sdk\include" "%~dp0ets2u_telemetry.cpp" ^
   /Fo"%~dp0bin\\" /Fe"%~dp0bin\ets2u_telemetry.dll" /link /DEF:"%~dp0ets2u_telemetry.def" || exit /b 1
echo Built %~dp0bin\ets2u_telemetry.dll
