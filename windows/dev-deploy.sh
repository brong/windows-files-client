#!/bin/bash
# Build and deploy FileNodeClient from current working tree (or a specified commit)
# Usage: ./dev-deploy.sh [commit]
#   No args: builds from current working tree
#   With arg: builds from that commit via worktree

set -e

SRCDIR="/home/brong/src/files-client"
BUILDDIR="/mnt/c/Users/brong/AppData/Local/Temp/files-client-build"
WORKTREE=""

if [ -n "$1" ]; then
    WORKTREE="/tmp/fnc-deploy-$$"
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

# Windows projects are under windows/ subdirectory
WINSRC="$SRCDIR/windows"

echo "=== Rsync to build dir ==="
rm -rf "$BUILDDIR"
rsync -a --exclude='.git' --exclude='.claude' --exclude='bin' --exclude='obj' "$WINSRC/" "$BUILDDIR/"

echo "=== Publishing Service ==="
cd "$BUILDDIR"
dotnet.exe publish FileNodeClient.Service/FileNodeClient.Service.csproj -c Release -r win-x64 --self-contained -o FileNodeClient.Package/publish 2>&1 | tail -3

echo "=== Publishing App ==="
dotnet.exe publish FileNodeClient.App/FileNodeClient.App.csproj -c Release -r win-x64 --self-contained -o FileNodeClient.Package/publish 2>&1 | tail -3

echo "=== Building native thumbnail DLL ==="
if [ -f "$WINSRC/FileNodeClient.ThumbnailExtension/ThumbnailHandler.c" ]; then
    x86_64-w64-mingw32-gcc -shared -O2 \
        -o "$BUILDDIR/FileNodeClient.Package/publish/FileNodeClient.ThumbnailExtension.dll" \
        "$WINSRC/FileNodeClient.ThumbnailExtension/ThumbnailHandler.c" \
        "$WINSRC/FileNodeClient.ThumbnailExtension/ThumbnailHandler.def" \
        -lole32 -lwindowscodecs -lgdi32 -luser32 -luuid
    echo "Native DLL built"
else
    echo "No ThumbnailHandler.c found, skipping native DLL"
fi

echo "=== Copying manifest and assets ==="
cp "$BUILDDIR/FileNodeClient.Package/AppxManifest.xml" "$BUILDDIR/FileNodeClient.Package/publish/"
cp -r "$BUILDDIR/FileNodeClient.Package/Assets" "$BUILDDIR/FileNodeClient.Package/publish/"

echo "=== Registering MSIX ==="
powershell.exe -NoProfile -Command 'Add-AppxPackage -Register "C:\Users\brong\AppData\Local\Temp\files-client-build\FileNodeClient.Package\publish\AppxManifest.xml"'

echo "=== Deploy complete ==="
