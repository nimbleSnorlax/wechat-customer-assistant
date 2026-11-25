@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
set "PYTHONPATH=%~dp0.python"
set "PYTHONUTF8=1"
python -u launcher.py --open
if errorlevel 1 pause
