@echo off
setlocal

if "%~2"=="" (
  echo Usage: exceldb-difftool.bat BASE.xlsx TARGET.xlsx [report.json]
  exit /b 3
)

if "%~3"=="" (
  exceldb.exe diff "%~1" "%~2"
) else (
  exceldb.exe diff "%~1" "%~2" --json "%~3"
)
exit /b %errorlevel%

