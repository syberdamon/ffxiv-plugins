@echo off
rem Patch-day launcher: point your XIVLauncher shortcut at this file (see README.md).
start "" /min powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0XIVLauncher-PatchDay.ps1" %*
