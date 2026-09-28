@echo off
setlocal EnableExtensions

set "SCRIPT_DIR=%~dp0"
pushd "%SCRIPT_DIR%.."

REM ================================================================
REM Build and push the HR Project images to private GHCR packages.
REM
REM Required environment variables:
REM   GHCR_USERNAME  GitHub login username used by docker login
REM   GHCR_PAT       Classic PAT with read:packages + write:packages
REM
REM Optional environment variables:
REM   GHCR_OWNER     Package owner/user/organization (defaults to username)
REM   GHCR_PROJECT   Image prefix (defaults to hr-project)
REM
REM Usage:
REM   docker\build-push-ghcr.bat 1.0.0
REM ================================================================

if "%GHCR_USERNAME%"=="" (
    echo [ERROR] GHCR_USERNAME is not set.
    echo Example: $env:GHCR_USERNAME = "your-github-username"
    popd
    exit /b 1
)

if "%GHCR_PAT%"=="" (
    echo [ERROR] GHCR_PAT is not set in this terminal.
    echo Set it securely before running this script. Never put it in this file.
    popd
    exit /b 1
)

if "%GHCR_OWNER%"=="" set "GHCR_OWNER=%GHCR_USERNAME%"
if "%GHCR_PROJECT%"=="" set "GHCR_PROJECT=hr-project"

set "IMAGE_TAG=%~1"
if "%IMAGE_TAG%"=="" set "IMAGE_TAG=1.0.0"

where docker >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Docker CLI was not found.
    popd
    exit /b 1
)

docker info >nul 2>&1
if errorlevel 1 (
    echo [ERROR] Docker Desktop is not running or this terminal cannot access Docker Engine.
    popd
    exit /b 1
)

echo.
echo ===== GHCR build configuration =====
echo Login user: %GHCR_USERNAME%
echo Owner:      %GHCR_OWNER%
echo Project:    %GHCR_PROJECT%
echo Tag:        %IMAGE_TAG%
echo Backend:    ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-backend:%IMAGE_TAG%
echo Frontend:   ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-frontend:%IMAGE_TAG%
echo Worker:     ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-worker:%IMAGE_TAG%
echo.

echo ===== Login to GHCR =====
<nul set /p "=%GHCR_PAT%" | docker login ghcr.io -u "%GHCR_USERNAME%" --password-stdin
if errorlevel 1 goto :failed

echo.
echo ===== Build backend =====
docker build --pull -f docker\Dockerfile.backend -t "ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-backend:%IMAGE_TAG%" .
if errorlevel 1 goto :failed

echo.
echo ===== Build frontend =====
docker build --pull -f docker\Dockerfile.frontend -t "ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-frontend:%IMAGE_TAG%" .
if errorlevel 1 goto :failed

echo.
echo ===== Build attendance worker =====
docker build --pull -f docker\Dockerfile.worker -t "ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-worker:%IMAGE_TAG%" .
if errorlevel 1 goto :failed

echo.
echo ===== Push backend =====
docker push "ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-backend:%IMAGE_TAG%"
if errorlevel 1 goto :failed

echo.
echo ===== Push frontend =====
docker push "ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-frontend:%IMAGE_TAG%"
if errorlevel 1 goto :failed

echo.
echo ===== Push attendance worker =====
docker push "ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-worker:%IMAGE_TAG%"
if errorlevel 1 goto :failed

echo.
echo ================================================================
echo Images pushed successfully.
echo ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-backend:%IMAGE_TAG%
echo ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-frontend:%IMAGE_TAG%
echo ghcr.io/%GHCR_OWNER%/%GHCR_PROJECT%-worker:%IMAGE_TAG%
echo ================================================================
popd
exit /b 0

:failed
echo.
echo [ERROR] Build or push failed. No token was written to disk.
popd
exit /b 1

