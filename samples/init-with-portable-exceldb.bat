@echo off
setlocal
set "TARGET=%~f1"
if "%~1"=="" set "TARGET=%CD%"

if "%~2"=="" (
  "%~dp0exceldb.exe" init "%TARGET%"
) else (
  "%~dp0exceldb.exe" init "%TARGET%" --generated-csharp-dir "%~f2"
)
exit /b %errorlevel%
