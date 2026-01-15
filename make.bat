@echo off

call :get_version

IF NOT "%VERSION%"=="" (
    call :print_version "Using Git describe format:"
    goto :build
)

IF "%1"=="" (
    SET VERSION=2.15.0
    call :print_version "Using default version:"
) ELSE (
    SET VERSION=%1
    call :print_version "Using version from command line:"
)

:build
SET PATH=%PATH%;"C:\Program Files (x86)\Inno Setup 6"

echo Building version %VERSION%...

dotnet publish LenovoLegionToolkit.WPF -c release -o build /p:DebugType=None /p:Version=%DOTNET_VERSION% /p:FileVersion=%DOTNET_VERSION% /p:SourceRevisionId="%COMMIT_HASH%+%BUILD_TIME%" || exit /b
dotnet publish LenovoLegionToolkit.SpectrumTester -c release -o build /p:DebugType=None /p:Version=%DOTNET_VERSION% /p:FileVersion=%DOTNET_VERSION% /p:SourceRevisionId="%COMMIT_HASH%+%BUILD_TIME%" || exit /b
dotnet publish LenovoLegionToolkit.CLI -c release -o build /p:DebugType=None /p:Version=%DOTNET_VERSION% /p:FileVersion=%DOTNET_VERSION% /p:SourceRevisionId="%COMMIT_HASH%+%BUILD_TIME%" || exit /b

iscc make_installer.iss /DMyAppVersion=%VERSION% /DMyAppVersionInfo=%VERSION_INFO% || exit /b

echo Build completed successfully! Version: %VERSION%
goto :eof

:get_version
SET VERSION=
SET VERSION_INFO=
SET DOTNET_VERSION=
SET COMMIT_HASH=
SET BUILD_TIME=

REM Check if current commit has an exact tag
git describe --tags --exact-match > git_tag_tmp.txt 2>nul
IF %ERRORLEVEL% EQU 0 (
    FOR /F "usebackq tokens=*" %%i IN ("git_tag_tmp.txt") DO SET VERSION=%%i
    del git_tag_tmp.txt
    call :remove_v_prefix
    SET VERSION_INFO=%VERSION%
    SET DOTNET_VERSION=%VERSION%.0
    REM Get commit hash
    git rev-parse --short HEAD > git_hash_tmp.txt 2>nul
    IF %ERRORLEVEL% EQU 0 (
        FOR /F "usebackq tokens=*" %%i IN ("git_hash_tmp.txt") DO SET COMMIT_HASH=%%i
        del git_hash_tmp.txt
    )
    REM Get build time
    for /f "tokens=2 delims==" %%a in ('wmic os get localdatetime /value') do set datetime=%%a
    set BUILD_TIME=%datetime:~0,4%-%datetime:~4,2%-%datetime:~6,2% %datetime:~8,2%:%datetime:~10,2%:%datetime:~12,2%
    goto :eof
)
IF EXIST git_tag_tmp.txt del git_tag_tmp.txt

REM If no exact tag, use git describe --tags --long
git describe --tags --long > git_tag_tmp.txt 2>nul
IF %ERRORLEVEL% EQU 0 (
    FOR /F "usebackq tokens=*" %%i IN ("git_tag_tmp.txt") DO SET VERSION=%%i
    del git_tag_tmp.txt
)
call :remove_v_prefix
REM Create a valid VersionInfoVersion (replace hyphens with dots)
SET VERSION_INFO=%VERSION:-=.%
REM Create a valid .NET version (extract major.minor.build.commit_count)
REM Format: 2.26.1-20-g0176d79d -> 2.26.1.20
REM Extract tag and commit count
FOR /F "tokens=1-3 delims=-" %%a IN ("%VERSION%") DO (
    SET TAG=%%a
    SET COMMIT_COUNT=%%b
    SET COMMIT_HASH=%%c
)
SET DOTNET_VERSION=%TAG%.%COMMIT_COUNT%
REM Get build time
for /f "tokens=2 delims==" %%a in ('wmic os get localdatetime /value') do set datetime=%%a
set BUILD_TIME=%datetime:~0,4%-%datetime:~4,2%-%datetime:~6,2% %datetime:~8,2%:%datetime:~10,2%:%datetime:~12,2%
goto :eof

:remove_v_prefix
IF "%VERSION:~0,1%"=="v" (
    SET VERSION=%VERSION:~1%
)
goto :eof

:print_version
echo %~1 %VERSION%
goto :eof