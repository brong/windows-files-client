@echo off
setlocal
pushd %~dp0

set "SRCDIR=%~dp0"
set "BUILDDIR=%SRCDIR%"

:: WiX linker needs the Windows Installer service, which requires a local drive.
:: Detect UNC paths (e.g. WSL2) and redirect the build to a temp directory.
echo %SRCDIR% | findstr /i "^\\\\" >nul 2>&1
if %errorlevel%==0 (
    set "BUILDDIR=%TEMP%\FastmailFiles-build"
    echo Detected UNC path, building in %BUILDDIR%...
    if exist "%BUILDDIR%" rmdir /s /q "%BUILDDIR%"
    mkdir "%BUILDDIR%"
)

echo Publishing FilesClient.Service...
dotnet publish ..\FilesClient.Service\FilesClient.Service.csproj -c Release -r win-x64 --self-contained -o "%BUILDDIR%publish\Service"
if errorlevel 1 goto :error

echo.
echo Publishing FilesClient.App...
dotnet publish ..\FilesClient.App\FilesClient.App.csproj -c Release -r win-x64 --self-contained -o "%BUILDDIR%publish\App"
if errorlevel 1 goto :error

:: Copy WiX project files to build dir if using temp
if not "%BUILDDIR%"=="%SRCDIR%" (
    copy /y "%SRCDIR%FilesClient.Installer.wixproj" "%BUILDDIR%" >nul
    copy /y "%SRCDIR%Package.wxs" "%BUILDDIR%" >nul
)

echo.
echo Building MSI installer...
pushd "%BUILDDIR%"
dotnet build FilesClient.Installer.wixproj -c Release
if errorlevel 1 (
    popd
    goto :error
)
popd

:: Copy MSI back if using temp
if not "%BUILDDIR%"=="%SRCDIR%" (
    if not exist "%SRCDIR%bin\Release" mkdir "%SRCDIR%bin\Release"
    copy /y "%BUILDDIR%bin\Release\FastmailFiles.msi" "%SRCDIR%bin\Release\" >nul
    echo Copied MSI to %SRCDIR%bin\Release\FastmailFiles.msi
)

echo.
echo Success: bin\Release\FastmailFiles.msi
popd
goto :eof

:error
echo.
echo Build failed!
popd
exit /b 1
