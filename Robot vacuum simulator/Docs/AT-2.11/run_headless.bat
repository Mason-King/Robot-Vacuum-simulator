@echo off
rem AT-2.11: runs job_faults.json headless and shows the result. No command-line knowledge needed: double-click it.
rem Expected: exit code 10 ("finished, some runs failed") and results.json listing the five deliberately broken
rem runs as failed, each with its reason, and the Boustrophedon Sweep run as succeeded.

setlocal
set "HERE=%~dp0"
set "OUT=%HERE%out"

echo Drag the simulator's .exe (from the build folder) into this window, then press Enter:
set /p "EXE="
set "EXE=%EXE:"=%"
if not exist "%EXE%" (
    echo Can't find "%EXE%".
    pause
    exit /b 1
)

echo.
if not exist "%OUT%" mkdir "%OUT%"
echo Running the AT-2.11 job headless. This takes about a minute (two of the runs hang on purpose)...
"%EXE%" -batchmode -nographics -configPath "%HERE%job_faults.json" -outputPath "%OUT%" -noResume -logFile "%OUT%\log.txt"
set "CODE=%ERRORLEVEL%"

echo.
echo Exit code: %CODE%   (0 = all runs succeeded, 10 = finished but some runs failed, 2 = job rejected, 1 = crashed)
if "%CODE%"=="10" (echo PASS: the expected exit code.) else (echo CHECK: expected 10.)
echo Results: "%OUT%\results.json"   Log: "%OUT%\log.txt"
echo.
if exist "%OUT%\results.json" start "" notepad "%OUT%\results.json"
pause
