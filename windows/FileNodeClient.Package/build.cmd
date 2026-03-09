@echo off
setlocal
pushd %~dp0

set "SRCDIR=%~dp0.."
set "BUILDDIR=%SRCDIR%"

:: MSIX tools need a local drive. Detect UNC paths (e.g. WSL2) and redirect.
echo %SRCDIR% | findstr /i "^\\\\" >nul 2>&1
if %errorlevel%==0 (
    set "BUILDDIR=%TEMP%\files-client-build"
    echo Detected UNC path, building in %BUILDDIR%...
    if exist "%BUILDDIR%" rmdir /s /q "%BUILDDIR%"
    mkdir "%BUILDDIR%"
    robocopy "%SRCDIR%" "%BUILDDIR%" /MIR /XD .git .claude bin obj /XF *.user >nul
)

:: ---- Step 1: Publish both App and Service into shared directory ----
:: Same directory so ServiceLauncher finds Service.exe via AppContext.BaseDirectory
echo Publishing FileNodeClient.Service...
dotnet publish "%BUILDDIR%\FileNodeClient.Service\FileNodeClient.Service.csproj" -c Release -r win-x64 --self-contained -o "%BUILDDIR%\FileNodeClient.Package\publish"
if errorlevel 1 goto :error

echo.
echo Publishing FileNodeClient.App...
dotnet publish "%BUILDDIR%\FileNodeClient.App\FileNodeClient.App.csproj" -c Release -r win-x64 --self-contained -o "%BUILDDIR%\FileNodeClient.Package\publish"
if errorlevel 1 goto :error

echo.
echo Building native ThumbnailExtension DLL...
call :build_thumbnail_dll
if errorlevel 1 goto :error

:: ---- Step 2: Create self-signed cert if needed ----
set "CERT_SUBJECT=CN=Fastmail Pty Ltd"
set "CERT_PFX=%BUILDDIR%\FileNodeClient.Package\DevCert.pfx"
if not exist "%CERT_PFX%" (
    echo.
    echo Creating self-signed dev certificate...
    powershell -NoProfile -Command ^
        "$cert = New-SelfSignedCertificate -Type Custom -Subject '%CERT_SUBJECT%' -KeyUsage DigitalSignature -FriendlyName 'FileNodeClient Dev' -CertStoreLocation 'Cert:\CurrentUser\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}'); Export-PfxCertificate -Cert $cert -FilePath '%CERT_PFX%' -Password (ConvertTo-SecureString -String 'devpass' -Force -AsPlainText)"
    if errorlevel 1 (
        echo WARNING: Certificate creation failed. You may need to create one manually.
    )
)

:: ---- Step 3: Build MSIX ----
echo.
echo Building MSIX package...
pushd "%BUILDDIR%\FileNodeClient.Package"

set "MSIX_OUTPUT=%BUILDDIR%\FileNodeClient.Package\bin\Release\FileNodeClient.msix"
if not exist "bin\Release" mkdir "bin\Release"

:: Create mapping file for makeappx — flat layout
(
echo [Files]
echo "AppxManifest.xml" "AppxManifest.xml"
echo "Assets\StoreLogo.png" "Assets\StoreLogo.png"
echo "Assets\Square44x44Logo.png" "Assets\Square44x44Logo.png"
echo "Assets\Square150x150Logo.png" "Assets\Square150x150Logo.png"
) > mapping.txt

:: Include all files from the shared publish directory
for %%f in (publish\*.exe) do (
    echo "%%f" "%%~nxf" >> mapping.txt
)
for %%f in (publish\*.dll) do (
    echo "%%f" "%%~nxf" >> mapping.txt
)
for %%f in (publish\*.json) do (
    echo "%%f" "%%~nxf" >> mapping.txt
)

makeappx pack /f mapping.txt /p "%MSIX_OUTPUT%" /o
if errorlevel 1 (
    popd
    goto :error
)
popd

:: ---- Step 4: Sign the MSIX ----
if exist "%CERT_PFX%" (
    echo.
    echo Signing MSIX...
    signtool sign /fd SHA256 /a /f "%CERT_PFX%" /p devpass "%MSIX_OUTPUT%"
    if errorlevel 1 (
        echo WARNING: Signing failed. Package will need manual signing.
    )
)

:: Copy MSIX back if using temp build dir
if not "%BUILDDIR%"=="%SRCDIR%" (
    if not exist "%~dp0bin\Release" mkdir "%~dp0bin\Release"
    copy /y "%MSIX_OUTPUT%" "%~dp0bin\Release\" >nul
    echo Copied MSIX to %~dp0bin\Release\FileNodeClient.msix
)

echo.
echo Success: bin\Release\FileNodeClient.msix
popd
goto :eof

:build_thumbnail_dll
:: Compile native C thumbnail handler DLL using MSVC
:: Locate vcvarsall.bat from Visual Studio
set "VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
if not exist "%VSWHERE%" (
    echo ERROR: vswhere.exe not found - Visual Studio required
    exit /b 1
)
for /f "usebackq tokens=*" %%i in (`"%VSWHERE%" -latest -property installationPath`) do set "VSDIR=%%i"
set "VCVARS=%VSDIR%\VC\Auxiliary\Build\vcvarsall.bat"
if not exist "%VCVARS%" (
    echo ERROR: vcvarsall.bat not found at %VCVARS%
    exit /b 1
)

:: Run cl.exe in a sub-shell with x64 environment
set "THUMB_SRC=%BUILDDIR%\FileNodeClient.ThumbnailExtension\ThumbnailHandler.c"
set "THUMB_DEF=%BUILDDIR%\FileNodeClient.ThumbnailExtension\ThumbnailHandler.def"
set "THUMB_OUT=%BUILDDIR%\FileNodeClient.Package\publish\FileNodeClient.ThumbnailExtension.dll"

cmd /c "call "%VCVARS%" x64 >nul 2>&1 && cl /LD /O2 /nologo "%THUMB_SRC%" /link /DEF:"%THUMB_DEF%" ole32.lib windowscodecs.lib gdi32.lib user32.lib /OUT:"%THUMB_OUT%" /IMPLIB:"%BUILDDIR%\FileNodeClient.ThumbnailExtension\ThumbnailHandler.lib""
if errorlevel 1 (
    echo ERROR: Native DLL compilation failed
    exit /b 1
)
echo Native ThumbnailExtension.dll built successfully
exit /b 0

:error
echo.
echo Build failed!
popd
exit /b 1
