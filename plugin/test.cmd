@echo off
rem Builds the plugin, then a fake game host that loads it and checks its shared memory.
setlocal
call "%~dp0build.cmd" || exit /b 1
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%i in (`call "%VSWHERE%" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath`) do set "VS=%%i"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set ROOT=%~dp0..
cl /nologo /EHsc /W4 /I"%ROOT%\tools\scs_sdk\include" "%~dp0test_host.cpp" /Fo"%~dp0bin\\" /Fe"%~dp0bin\test_host.exe" || exit /b 1
rem Optional: test.cmd 0 drive 20000  keeps "driving" for 20 s so the overlay can be checked.
"%~dp0bin\test_host.exe" "%~dp0bin\ets2u_telemetry.dll" %*
