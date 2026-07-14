@echo off
setlocal
set "TARGET=%~f1"
if "%~1"=="" set "TARGET=%CD%"

"%~dp0exceldb.exe" init "%TARGET%"
exit /b %errorlevel%
