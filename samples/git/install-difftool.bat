@echo off
setlocal

set "SCRIPT=%~dp0exceldb-difftool.bat"
git config --local diff.exceldb.command "\"%SCRIPT%\" \"$LOCAL\" \"$REMOTE\""
if errorlevel 1 exit /b %errorlevel%
git config --local difftool.exceldb.cmd "\"%SCRIPT%\" \"$LOCAL\" \"$REMOTE\""
if errorlevel 1 exit /b %errorlevel%

echo ExcelDB difftool installed for this repository.
echo Run: git difftool --tool=exceldb -- path\to\workbook.xlsx

