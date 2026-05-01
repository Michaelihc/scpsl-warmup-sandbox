@echo off
setlocal

cd /d "%~dp0\..\docs"

where python >nul 2>nul
if %errorlevel% neq 0 (
  echo Python was not found on PATH.
  echo Open docs\server-list-preview.html directly, or install Python to use localhost preview.
  pause
  exit /b 1
)

start "" "http://127.0.0.1:4173/server-list-preview.html"
python -m http.server 4173 --bind 127.0.0.1
