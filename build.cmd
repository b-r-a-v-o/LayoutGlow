@echo off
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
"%CSC%" -nologo -target:winexe -out:"%~dp0LayoutGlow.exe" "%~dp0src\LayoutGlow.cs" -r:System.Windows.Forms.dll -r:System.Drawing.dll
if %ERRORLEVEL%==0 (echo BUILD OK: %~dp0LayoutGlow.exe) else (echo BUILD FAILED)
pause
