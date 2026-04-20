#!/bin/bash
# Build a signed MSIX package from WSL2
# Usage: ./build-msix.sh [--install] [commit]
#   No args: builds from current working tree
#   --install: install the MSIX after building (stops running app)
#   With commit arg: builds from that commit via worktree
# Output: FileNodeClient.Package/bin/Release/FileNodeClient.msix

set -e

INSTALL=false
if [ "$1" = "--install" ]; then
    INSTALL=true
    shift
fi

SRCDIR="/home/brong/src/files-client"
BUILDDIR="/mnt/c/Users/brong/AppData/Local/Temp/files-client-msix"
WORKTREE=""

if [ -n "$1" ]; then
    WORKTREE="/tmp/fnc-msix-$$"
    echo "=== Creating worktree at $1 ==="
    cd "$SRCDIR"
    git worktree add "$WORKTREE" "$1"
    SRCDIR="$WORKTREE"
fi

cleanup_worktree() {
    if [ -n "$WORKTREE" ] && [ -d "$WORKTREE" ]; then
        cd /home/brong/src/files-client
        git worktree remove "$WORKTREE" 2>/dev/null || true
    fi
}
trap cleanup_worktree EXIT

WINSRC="$SRCDIR/windows"

echo "=== Rsync to build dir ==="
rm -rf "$BUILDDIR"
rsync -a --exclude='.git' --exclude='.claude' --exclude='bin' --exclude='obj' --exclude='FileNodeClient.Package/publish' "$WINSRC/" "$BUILDDIR/"

WINBUILD=$(wslpath -w "$BUILDDIR")
PUBLISHDIR="$BUILDDIR/FileNodeClient.Package/publish"

# Extract version from VERSION file (first non-comment line, first field)
APP_VERSION=$(grep -v '^#\|^$' "$BUILDDIR/VERSION" | head -1 | awk '{print $1}')
BUILD_DATE=$(date -u +%Y-%m-%dT%H:%M:%SZ)
echo "=== Version: $APP_VERSION  Build: $BUILD_DATE ==="
VERSION_PROPS="-p:Version=$APP_VERSION -p:InformationalVersion=$APP_VERSION+$BUILD_DATE"

echo "=== Publishing Service ==="
cd "$BUILDDIR"
dotnet.exe publish FileNodeClient.Service/FileNodeClient.Service.csproj -c Release -r win-x64 --self-contained -o FileNodeClient.Package/publish $VERSION_PROPS 2>&1 | tail -3

echo "=== Publishing App ==="
dotnet.exe publish FileNodeClient.App/FileNodeClient.App.csproj -c Release -r win-x64 --self-contained -o FileNodeClient.Package/publish $VERSION_PROPS 2>&1 | tail -3

echo "=== Stripping debug/diagnostic/design-time DLLs ==="
rm -f "$PUBLISHDIR"/mscordaccore*.dll \
      "$PUBLISHDIR"/mscordbi.dll \
      "$PUBLISHDIR"/Microsoft.DiaSymReader.Native.amd64.dll \
      "$PUBLISHDIR"/createdump.exe \
      "$PUBLISHDIR"/System.Windows.Forms.Design.dll \
      "$PUBLISHDIR"/System.DirectoryServices.dll \
      "$PUBLISHDIR"/Microsoft.CSharp.dll
echo "Removed debug/design-time DLLs"

echo "=== Building native thumbnail DLL ==="
if [ -f "$WINSRC/FileNodeClient.ThumbnailExtension/ThumbnailHandler.c" ]; then
    x86_64-w64-mingw32-gcc -shared -O2 \
        -o "$PUBLISHDIR/FileNodeClient.ThumbnailExtension.dll" \
        "$WINSRC/FileNodeClient.ThumbnailExtension/ThumbnailHandler.c" \
        "$WINSRC/FileNodeClient.ThumbnailExtension/ThumbnailHandler.def" \
        -lole32 -lwindowscodecs -lgdi32 -luser32 -luuid
    echo "Native DLL built"
else
    echo "No ThumbnailHandler.c found, skipping native DLL"
fi

echo "=== Copying manifest and assets ==="
cp "$BUILDDIR/FileNodeClient.Package/AppxManifest.xml" "$PUBLISHDIR/"
cp -r "$BUILDDIR/FileNodeClient.Package/Assets" "$PUBLISHDIR/"

# Find Windows SDK tools
SDKBIN=$(powershell.exe -NoProfile -Command '
    $root = Join-Path ${env:ProgramFiles(x86)} "Windows Kits\10\bin"
    if (Test-Path $root) {
        Get-ChildItem $root -Directory -Filter "10.*" |
            Sort-Object Name -Descending |
            ForEach-Object {
                $x = Join-Path $_.FullName "x64\makeappx.exe"
                if (Test-Path $x) { Split-Path $x; break }
            }
    }
' | tr -d '\r')

if [ -z "$SDKBIN" ]; then
    echo "ERROR: Windows SDK not found (need makeappx.exe)"
    exit 1
fi
echo "Using SDK tools from: $SDKBIN"
# Convert Windows path to WSL path for direct invocation
SDKBIN_WSL=$(wslpath "$SDKBIN")

echo "=== Creating self-signed cert (if needed) ==="
# Store cert in a persistent location so it's reused across builds
# (the build dir is wiped each time, but the cert must stay the same
# to avoid re-importing into TrustedPeople on every build)
CERT_STORE="$HOME/.fastmail"
CERT_PFX_PERSIST="$CERT_STORE/DevCert.pfx"
CERT_PFX="$BUILDDIR/FileNodeClient.Package/DevCert.pfx"
mkdir -p "$CERT_STORE"
if [ -f "$CERT_PFX_PERSIST" ]; then
    cp "$CERT_PFX_PERSIST" "$CERT_PFX"
else
    powershell.exe -NoProfile -Command "
        \$cert = New-SelfSignedCertificate -Type Custom -Subject 'CN=Fastmail Pty Ltd' -KeyUsage DigitalSignature -FriendlyName 'FileNodeClient Dev' -CertStoreLocation 'Cert:\CurrentUser\\My' -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3','2.5.29.19={text}')
        Export-PfxCertificate -Cert \$cert -FilePath '$(wslpath -w "$CERT_PFX")' -Password (ConvertTo-SecureString -String 'devpass' -Force -AsPlainText)
    " || echo "WARNING: Certificate creation failed"
    cp "$CERT_PFX" "$CERT_PFX_PERSIST"
fi

echo "=== Building mapping file ==="
PKGDIR="$BUILDDIR/FileNodeClient.Package"
WINPKGDIR=$(wslpath -w "$PKGDIR")
MAPFILE="$PKGDIR/mapping.txt"
{
    echo "[Files]"
    echo "\"$WINPKGDIR\\AppxManifest.xml\" \"AppxManifest.xml\""
    echo "\"$WINPKGDIR\\Assets\\StoreLogo.png\" \"Assets\\StoreLogo.png\""
    echo "\"$WINPKGDIR\\Assets\\Square44x44Logo.png\" \"Assets\\Square44x44Logo.png\""
    echo "\"$WINPKGDIR\\Assets\\Square150x150Logo.png\" \"Assets\\Square150x150Logo.png\""
    for f in "$PUBLISHDIR"/*.exe; do
        [ -f "$f" ] && echo "\"$WINPKGDIR\\publish\\$(basename "$f")\" \"$(basename "$f")\""
    done
    for f in "$PUBLISHDIR"/*.dll; do
        [ -f "$f" ] && echo "\"$WINPKGDIR\\publish\\$(basename "$f")\" \"$(basename "$f")\""
    done
    for f in "$PUBLISHDIR"/*.json; do
        [ -f "$f" ] && echo "\"$WINPKGDIR\\publish\\$(basename "$f")\" \"$(basename "$f")\""
    done
} > "$MAPFILE"

echo "=== Packing MSIX ==="
mkdir -p "$PKGDIR/bin/Release"
MSIX_OUTPUT="$PKGDIR/bin/Release/FileNodeClient.msix"
WINMSIX=$(wslpath -w "$MSIX_OUTPUT")

"$SDKBIN_WSL/makeappx.exe" pack /f "$(wslpath -w "$MAPFILE")" /p "$WINMSIX" /o

echo "=== Signing MSIX ==="
if [ -f "$CERT_PFX" ]; then
    "$SDKBIN_WSL/signtool.exe" sign /fd SHA256 /a /f "$(wslpath -w "$CERT_PFX")" /p devpass "$WINMSIX"
fi

# Copy back to source tree
mkdir -p "$WINSRC/FileNodeClient.Package/bin/Release"
cp "$MSIX_OUTPUT" "$WINSRC/FileNodeClient.Package/bin/Release/"

if [ "$INSTALL" = true ]; then
    echo "=== Installing MSIX ==="
    powershell.exe -NoProfile -Command "Add-AppxPackage -Path '$(wslpath -w "$WINSRC/FileNodeClient.Package/bin/Release/FileNodeClient.msix")' -ForceUpdateFromAnyVersion -ForceApplicationShutdown"
    echo "=== Installed ==="
fi

# Create distributable zip with installer
echo "=== Creating distributable zip ==="
DIST_DIR="$WINSRC/FileNodeClient.Package/bin/Release/dist"
rm -rf "$DIST_DIR"
mkdir -p "$DIST_DIR"
cp "$WINSRC/FileNodeClient.Package/bin/Release/FileNodeClient.msix" "$DIST_DIR/"
cp "$WINSRC/install.ps1" "$DIST_DIR/"
cp "$WINSRC/Install FileNodeClient.bat" "$DIST_DIR/"
ZIPFILE="$WINSRC/FileNodeClient.Package/bin/Release/FileNodeClient-${APP_VERSION}.zip"
rm -f "$ZIPFILE"
(cd "$DIST_DIR" && powershell.exe -NoProfile -Command "Compress-Archive -Path '.\*' -DestinationPath '$(wslpath -w "$ZIPFILE")'")
rm -rf "$DIST_DIR"
echo "Distributable: FileNodeClient.Package/bin/Release/FileNodeClient-${APP_VERSION}.zip"

echo ""
echo "=== Success: FileNodeClient.Package/bin/Release/FileNodeClient.msix ==="
