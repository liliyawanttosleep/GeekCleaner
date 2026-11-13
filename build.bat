@echo off
rem Rebuild GeekCleaner.exe using the C# compiler that ships with Windows (.NET Framework 4.x)
cd /d "%~dp0"
set CSC="%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
set SRC=src\Common.cs src\Force.cs src\Apps.cs src\Leftovers.cs src\Junk.cs src\Analyze.cs src\Theme.cs src\LeftoverForm.cs src\MainForm.cs

rem 1) regenerate the icon from the vector drawing code in src\Theme.cs (class Logo)
%CSC% /nologo /nowarn:0649 /target:exe /platform:x64 /codepage:65001 /main:GeekCleaner.IconGen /out:tools\icongen.exe tools\IconGen.cs src\Theme.cs src\Common.cs src\Force.cs
if errorlevel 1 (echo ICON TOOL BUILD FAILED & exit /b 1)
tools\icongen.exe src\app.ico

rem 2) build the app
%CSC% /nologo /target:winexe /platform:x64 /optimize+ /codepage:65001 /main:GeekCleaner.Program ^
  /win32manifest:src\app.manifest /win32icon:src\app.ico /resource:src\app.ico,GeekCleaner.app.ico ^
  /out:GeekCleaner.exe %SRC%
if errorlevel 1 (echo BUILD FAILED) else (echo BUILD OK: GeekCleaner.exe)
