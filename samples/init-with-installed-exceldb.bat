@echo off
setlocal
set "TARGET=%~f1"
if "%~1"=="" set "TARGET=%CD%"

exceldb init "%TARGET%"
exit /b %errorlevel%
