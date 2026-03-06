@echo off
setlocal
pushd %~dp0

set "SRCDIR=%~dp0"
set "BUILDDIR=%SRCDIR%"

:: WiX linker needs the Windows Installer service, which requires a local drive.
:: Detect UNC paths (e.g. WSL2) and redirect the build to a temp directory.
echo %SRCDIR% | findstr /i "^\\\\" >nul 2>&1
if %errorlevel%==0 (
    set "BUILDDIR=%TEMP%\FileNodeClient-build"
    echo Detected UNC path, building in %BUILDDIR%...
    if exist "%BUILDDIR%" rmdir /s /q "%BUILDDIR%"
    mkdir "%BUILDDIR%"
)

echo Publishing FileNodeClient.Service...
dotnet publish ..\FileNodeClient.Service\FileNodeClient.Service.csproj -c Release -r win-x64 --self-contained -p:PublishTrimmed=true -p:TrimMode=partial -o "%BUILDDIR%publish\Shared"
if errorlevel 1 goto :error

echo.
echo Publishing FileNodeClient.App...
dotnet publish ..\FileNodeClient.App\FileNodeClient.App.csproj -c Release -r win-x64 --self-contained -p:PublishTrimmed=true -p:TrimMode=partial -o "%BUILDDIR%publish\Shared"
if errorlevel 1 goto :error

:: Copy WiX project files to build dir if using temp
if not "%BUILDDIR%"=="%SRCDIR%" (
    copy /y "%SRCDIR%FileNodeClient.Installer.wixproj" "%BUILDDIR%" >nul
    copy /y "%SRCDIR%Package.wxs" "%BUILDDIR%" >nul
)

echo.
echo Building MSI installer...
pushd "%BUILDDIR%"
dotnet build FileNodeClient.Installer.wixproj -c Release
if errorlevel 1 (
    popd
    goto :error
)
popd

:: Copy MSI back if using temp
if not "%BUILDDIR%"=="%SRCDIR%" (
    if not exist "%SRCDIR%bin\Release" mkdir "%SRCDIR%bin\Release"
    copy /y "%BUILDDIR%bin\Release\FileNodeClient.msi" "%SRCDIR%bin\Release\" >nul
    echo Copied MSI to %SRCDIR%bin\Release\FileNodeClient.msi
)

echo.
echo Success: bin\Release\FileNodeClient.msi
popd
goto :eof

:error
echo.
echo Build failed!
popd
exit /b 1
