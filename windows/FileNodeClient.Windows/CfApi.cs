using System.Runtime.InteropServices;
using System.Text;
using FileNodeClient.Logging;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FileNodeClient.Windows;

/// <summary>
/// The cfapi P/Invoke surface for individual placeholders: convert, update identity,
/// mark in-sync / always-full, read identity, hydrate, dehydrate, pin state. Stateless
/// wrappers over CfXxx calls with the handle-opening rules that make them work
/// (GENERIC_WRITE without READ so a dehydrated file is never fetched by us; a
/// single-attempt open for populate; retries for transient sharing violations).
/// Moved out of SyncEngine (SIMPLIFICATION.md Phase 4 step 5) so the engine holds
/// sync logic and this holds the OS calls.
/// </summary>
internal static class CfApi
{
    internal const uint FILE_WRITE_ATTRIBUTES = 0x100;
    internal const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;
    internal const uint GENERIC_WRITE = 0x40000000;

    internal static unsafe void ClearPinState(string filePath)
    {
        using var safeHandle = OpenWithRetry(filePath);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        PInvoke.CfSetPinState(
            handle,
            CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED,
            CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE,
            null    // synchronous
        ).ThrowOnFailure();
    }

    internal static unsafe void DehydratePlaceholder(string filePath)
    {
        using var safeHandle = OpenWithRetry(filePath);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        // Use CfUpdatePlaceholder with DEHYDRATE + MARK_IN_SYNC so the file
        // is atomically dehydrated and marked in-sync in one call. This avoids
        // the window where a dehydrated-but-not-in-sync file triggers Explorer
        // to send FETCH_DATA, and avoids TransferError marking it not-in-sync.
        long usn = 0;
        PInvoke.CfUpdatePlaceholder(
            handle,
            null,   // no metadata update
            null,   // keep existing identity
            0,
            null,   // no dehydrate range (dehydrate whole file)
            0,
            CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE
                | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC,
            &usn,
            null    // synchronous
        ).ThrowOnFailure();
    }

    /// <summary>
    /// Dehydrate a single file, retrying up to 5 times with 1-second delays
    /// for transient failures (e.g. 0x80070187 "cloud files in use").
    /// </summary>
    internal static void DehydratePlaceholderWithRetry(string filePath)
    {
        const int maxRetries = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                DehydratePlaceholder(filePath);
                if (attempt > 0)
                    Log.Info($"  Dehydration retry {attempt} succeeded for {Path.GetFileName(filePath)}");
                return;
            }
            catch when (attempt < maxRetries - 1)
            {
                Thread.Sleep(1000);
            }
        }
    }

    internal static unsafe void HydratePlaceholder(string filePath)
    {
        using var safeHandle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        PInvoke.CfHydratePlaceholder(
            handle,
            0,      // start offset
            -1,     // entire file
            CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE,
            null    // synchronous
        ).ThrowOnFailure();
    }

    /// <summary>
    /// Like ConvertToPlaceholder but with a single open attempt (no retry).
    /// Used during initial populate where blocking on locked directories is unacceptable.
    /// </summary>
    internal static unsafe void ConvertToPlaceholderNoRetry(string filePath, string nodeId, bool isDirectory = false)
    {
        var identityBytes = Encoding.UTF8.GetBytes(nodeId);
        using var safeHandle = OpenNoRetry(filePath, isDirectory);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        fixed (byte* pIdentity = identityBytes)
        {
            var flags = CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC;
            if (isDirectory)
                flags |= CF_CONVERT_FLAGS.CF_CONVERT_FLAG_ALWAYS_FULL;

            long usn = 0;
            PInvoke.CfConvertToPlaceholder(
                handle,
                pIdentity,
                (uint)identityBytes.Length,
                flags,
                &usn,
                null).ThrowOnFailure();
        }
    }

    internal static unsafe void ConvertToPlaceholder(string filePath, string nodeId, bool isDirectory = false)
    {
        var identityBytes = Encoding.UTF8.GetBytes(nodeId);
        using var safeHandle = OpenWithRetry(filePath, isDirectory);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        fixed (byte* pIdentity = identityBytes)
        {
            var flags = CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC;
            if (isDirectory)
                flags |= CF_CONVERT_FLAGS.CF_CONVERT_FLAG_ALWAYS_FULL;

            long usn = 0;
            PInvoke.CfConvertToPlaceholder(
                handle,
                pIdentity,
                (uint)identityBytes.Length,
                flags,
                &usn,
                null).ThrowOnFailure();
        }
    }

    /// <summary>
    /// Ensures a file/directory is a placeholder with the given identity.
    /// Tries ConvertToPlaceholder first; if it fails because the file is
    /// already a placeholder (0x8007017C), falls back to UpdatePlaceholderIdentity.
    /// </summary>
    internal static void EnsurePlaceholder(string filePath, string nodeId, bool isDirectory = false)
    {
        try
        {
            ConvertToPlaceholder(filePath, nodeId, isDirectory);
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x8007017C))
        {
            // ERROR_CLOUD_OPERATION_INVALID — file is already a placeholder
            Log.Info($"CfApi: file already a placeholder, updating identity: {Path.GetFileName(filePath)}");
            UpdatePlaceholderIdentity(filePath, nodeId, isDirectory);
        }
    }

    internal static unsafe void UpdatePlaceholderIdentity(string filePath, string newNodeId, bool isDirectory = false)
    {
        var identityBytes = Encoding.UTF8.GetBytes(newNodeId);
        using var safeHandle = OpenWithRetry(filePath, isDirectory);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        fixed (byte* pIdentity = identityBytes)
        {
            long usn = 0;
            PInvoke.CfUpdatePlaceholder(
                handle,
                null,   // no metadata update
                pIdentity,
                (uint)identityBytes.Length,
                null,   // no dehydrate range
                0,
                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC,
                &usn,
                null).ThrowOnFailure();
        }
    }

    internal static unsafe void MarkDirectoryAlwaysFull(string dirPath)
    {
        using var safeHandle = OpenWithRetry(dirPath, isDirectory: true);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        long usn = 0;
        PInvoke.CfUpdatePlaceholder(
            handle,
            null,   // no metadata update
            null,   // keep existing identity
            0,
            null,   // no dehydrate range
            0,
            CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC
                | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_ENABLE_ON_DEMAND_POPULATION
                | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_ALWAYS_FULL,
            &usn,
            null).ThrowOnFailure();
    }

    internal static unsafe void SetInSync(string path)
    {
        SetSyncState(path, CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC);
    }

    internal static unsafe void SetNotInSync(string path)
    {
        SetSyncState(path, CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_NOT_IN_SYNC);
    }

    internal static unsafe void SetSyncState(string path, CF_IN_SYNC_STATE state)
    {
        // Use FILE_WRITE_ATTRIBUTES to avoid triggering hydration on dehydrated
        // files. GENERIC_READ/GENERIC_WRITE would cause cfapi to send FETCH_DATA.
        var isDirectory = Directory.Exists(path);
        var flags = isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0u;

        using var handle = PInvoke.CreateFile(
            path,
            FILE_WRITE_ATTRIBUTES,
            global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_READ
                | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_WRITE
                | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            global::Windows.Win32.Storage.FileSystem.FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            (global::Windows.Win32.Storage.FileSystem.FILE_FLAGS_AND_ATTRIBUTES)flags,
            null);

        var cfHandle = new global::Windows.Win32.Foundation.HANDLE(handle.DangerousGetHandle());
        PInvoke.CfSetInSyncState(
            cfHandle,
            state,
            CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
            null).ThrowOnFailure();
    }

    internal static unsafe string? ReadPlaceholderNodeId(string path)
    {
        try
        {
            var options = Directory.Exists(path) ? (FileOptions)0x02000000 : FileOptions.None;
            using var safeHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, options);
            var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());

            var buffer = new byte[256];
            fixed (byte* pBuffer = buffer)
            {
                uint returnedLen;
                var hr = PInvoke.CfGetPlaceholderInfo(
                    handle,
                    CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_BASIC,
                    pBuffer,
                    (uint)buffer.Length,
                    &returnedLen);

                if (hr.Failed)
                    return null;

                // CF_PLACEHOLDER_BASIC_INFO layout:
                //   PinState (int, offset 0)
                //   InSyncState (int, offset 4)
                //   FileId (long, offset 8)
                //   SyncRootFileId (long, offset 16)
                //   FileIdentityLength (uint, offset 24)
                //   FileIdentity (byte[], offset 28)
                const int fileIdentityLengthOffset = 24;
                const int fileIdentityOffset = 28;

                if (returnedLen < (uint)fileIdentityOffset)
                    return null;

                var identityLength = *(uint*)(pBuffer + fileIdentityLengthOffset);
                if (identityLength == 0 || returnedLen < (uint)fileIdentityOffset + identityLength)
                    return null;

                return Encoding.UTF8.GetString(pBuffer + fileIdentityOffset, (int)identityLength);
            }
        }
        catch
        {
            return null;
        }
    }

    internal static void StripZoneIdentifier(string filePath)
    {
        try { File.Delete(filePath + ":Zone.Identifier"); } catch { }
    }

    /// <summary>
    /// Open a file handle suitable for cfapi operations (CfUpdatePlaceholder,
    /// CfConvertToPlaceholder, etc.) WITHOUT triggering hydration on dehydrated
    /// placeholders.  Uses GENERIC_WRITE (not GENERIC_READ | GENERIC_WRITE)
    /// because GENERIC_READ on a dehydrated placeholder triggers FETCH_DATA.
    /// </summary>
    internal static unsafe Microsoft.Win32.SafeHandles.SafeFileHandle OpenNoRetry(string filePath, bool isDirectory = false)
    {
        var flags = isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0u;
        var handle = PInvoke.CreateFile(
            filePath,
            GENERIC_WRITE,
            global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_READ
                | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_WRITE
                | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            global::Windows.Win32.Storage.FileSystem.FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            (global::Windows.Win32.Storage.FileSystem.FILE_FLAGS_AND_ATTRIBUTES)flags,
            null);

        if (handle.IsInvalid)
            throw new IOException($"CreateFile failed for {filePath}");

        return new Microsoft.Win32.SafeHandles.SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: true);
    }

    internal static unsafe Microsoft.Win32.SafeHandles.SafeFileHandle OpenWithRetry(string filePath, bool isDirectory = false)
    {
        const int maxRetries = 5;
        var flags = isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0u;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var handle = PInvoke.CreateFile(
                    filePath,
                    GENERIC_WRITE,
                    global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_READ
                        | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_WRITE
                        | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_DELETE,
                    null,
                    global::Windows.Win32.Storage.FileSystem.FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                    (global::Windows.Win32.Storage.FileSystem.FILE_FLAGS_AND_ATTRIBUTES)flags,
                    null);

                if (handle.IsInvalid)
                    throw new IOException($"CreateFile failed for {filePath}");

                // Wrap in SafeFileHandle for automatic disposal
                return new Microsoft.Win32.SafeHandles.SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: true);
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }
}
