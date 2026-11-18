@echo off
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize /target:winexe /out:BtBattery.exe /win32manifest:app.manifest /win32icon:docs\app.ico /r:System.Windows.Forms.dll /r:System.Drawing.dll BtBattery.cs
