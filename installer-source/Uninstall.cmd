@echo off
setlocal
chcp 65001 >nul
"%~dp0Core\P5SPatch.exe" uninstall %*
set "P5S_PATCH_EXIT=%ERRORLEVEL%"
if not "%P5S_PATCH_NO_PAUSE%"=="1" pause
exit /b %P5S_PATCH_EXIT%
