@echo off
REM SimpleZipDrive launcher with portable path
REM Uses relative path from the batch file location

set "ZIPFILE=%~dp0TestFile.zip"

if not exist "%ZIPFILE%" (
    echo Error: Test file not found at: %ZIPFILE%
    echo Please ensure TestFile.zip exists in the same folder as this batch file.
    pause
    exit /b 1
)

SimpleZipDrive.exe "%ZIPFILE%" m

pause
