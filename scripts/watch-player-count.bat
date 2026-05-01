@echo off
setlocal

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0watch-player-count.ps1" %*
