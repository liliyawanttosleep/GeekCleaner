@echo off
rem Build a NON-elevated copy and capture screenshots of the three pages (read-only scans of drive D).
rem Output: tools\out\apps.png, junk.png, disk.png  (git-ignored: they show the real machine's programs and paths)
cd /d "%~dp0.."
if not exist tools\out mkdir tools\out
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set SRC=src\Common.cs src\Apps.cs src\Leftovers.cs src\Junk.cs src\Analyze.cs src\Theme.cs src\LeftoverForm.cs src\MainForm.cs
%CSC% /nologo /nowarn:0649 /target:winexe /platform:x64 /codepage:65001 /main:GeekCleaner.Program ^
  /win32manifest:tools\test.manifest /win32icon:src\app.ico /resource:src\app.ico,GeekCleaner.app.ico ^
  /out:tools\out\GeekCleaner-dev.exe %SRC%
if errorlevel 1 (echo BUILD FAILED & exit /b 1)
start /wait "" tools\out\GeekCleaner-dev.exe --shot "%CD%\tools\out"
echo Screenshots written to tools\out
