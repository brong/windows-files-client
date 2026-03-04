@echo off
setlocal
pushd %~dp0

echo Publishing FilesClient.Service...
dotnet publish ..\FilesClient.Service\FilesClient.Service.csproj -c Release -r win-x64 --self-contained -o publish\Service
if errorlevel 1 goto :error

echo.
echo Publishing FilesClient.App...
dotnet publish ..\FilesClient.App\FilesClient.App.csproj -c Release -r win-x64 --self-contained -o publish\App
if errorlevel 1 goto :error

echo.
echo Building MSI installer...
dotnet build FilesClient.Installer.wixproj -c Release
if errorlevel 1 goto :error

echo.
echo Success: bin\Release\FastmailFiles.msi
popd
goto :eof

:error
echo.
echo Build failed!
popd
exit /b 1
