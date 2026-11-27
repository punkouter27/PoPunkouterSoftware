@echo off
rem ============================================================
rem  Starts the local helper the /global pages talk to (http://127.0.0.1:5050).
rem  Leave this window open while you use the buttons on the site.
rem  The token it prints is pasted once on the site's "This PC" page.
rem ============================================================
setlocal
title PoPunkouterSoftware local helper
set "HERE=%~dp0"
set "PY=%HERE%..\..\..\PoBox\.venv\Scripts\python.exe"
if exist "%PY%" (
    "%PY%" "%HERE%local_helper.py" %*
) else (
    py -3 "%HERE%local_helper.py" %*
)
echo.
pause
