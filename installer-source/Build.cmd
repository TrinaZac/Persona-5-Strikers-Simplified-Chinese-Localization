@echo off
setlocal
set "P5S_REVIEW_CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%P5S_REVIEW_CSC%" (
  echo .NET Framework 4.8 x64 compiler was not found.
  exit /b 1
)
if not exist "%~dp0build\Core" mkdir "%~dp0build\Core"
"%P5S_REVIEW_CSC%" /nologo /optimize+ /platform:x64 /target:exe /r:System.Web.Extensions.dll /r:System.Windows.Forms.dll /r:System.Core.dll /resource:"%~dp0src\Credits.txt",P5S.Credits.txt /out:"%~dp0build\Core\P5SPatch.exe" "%~dp0src\P5SPatch.cs"
if errorlevel 1 exit /b 1
copy /y "%~dp0runtime\P5SPatch.exe.config" "%~dp0build\Core\P5SPatch.exe.config" >nul
copy /y "%~dp0review\manifest.json" "%~dp0build\Core\manifest.json" >nul
copy /y "%~dp0Install.cmd" "%~dp0build\Install.cmd" >nul
copy /y "%~dp0Uninstall.cmd" "%~dp0build\Uninstall.cmd" >nul
echo BUILD_SUCCESS: build\Core\P5SPatch.exe
echo Review help with: build\Core\P5SPatch.exe --help
echo No game files have been modified.
exit /b 0
