@echo off
setlocal

where exceldb.exe >nul 2>nul
if errorlevel 1 (
  echo ExcelDB: exceldb.exe is not available on PATH.
  exit /b 3
)

exceldb.exe schema build --check
if errorlevel 1 exit /b %errorlevel%

exceldb.exe check
exit /b %errorlevel%

