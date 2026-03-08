using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;
using FileNodeClient.Logging;
using FileNodeClient.Jmap.Models;

namespace FileNodeClient.Windows;

internal class PlaceholderManager
{
    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly string _syncRootPath;
    private readonly string _logPrefix;

    public PlaceholderManager(string syncRootPath, string logPrefix)
    {
        _syncRootPath = syncRootPath;
        _logPrefix = logPrefix;
    }

    // Names that are reserved path components on Windows — a folder named ".."
    // would resolve to the parent directory via Path.Combine.
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".", "..",
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Replace characters that are invalid in Windows filenames with underscore,
    /// and prefix reserved names (., .., CON, etc.) to avoid path traversal
    /// or device name collisions.
    /// </summary>
    internal static string SanitizeName(string name)
    {
        var result = name;

        if (result.IndexOfAny(InvalidChars) >= 0)
        {
            var chars = result.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                if (Array.IndexOf(InvalidChars, chars[i]) >= 0)
                    chars[i] = '_';
            }
            result = new string(chars);
        }

        result = result.TrimEnd(' ', '.');

        // After trimming, check if empty or a reserved name (e.g. ".." or "CON").
        // Prefix with underscore to avoid path traversal or device name collisions.
        if (result.Length == 0 || ReservedNames.Contains(result))
            result = "_" + result;

        return result;
    }

    public unsafe void CreatePlaceholders(string parentPath, FileNode[] children)
    {
        if (children.Length == 0)
            return;

        var infos = new CF_PLACEHOLDER_CREATE_INFO[children.Length];
        var pinnedIdentities = new GCHandle[children.Length];
        var pinnedNames = new GCHandle[children.Length];

        try
        {
            for (int i = 0; i < children.Length; i++)
            {
                var node = children[i];

                // Pin the name string — PCWSTR needs a char*
                var nameChars = (SanitizeName(node.Name) + "\0").ToCharArray();
                pinnedNames[i] = GCHandle.Alloc(nameChars, GCHandleType.Pinned);

                // Identity blob: store the FileNode ID as UTF-8
                var identityBytes = Encoding.UTF8.GetBytes(node.Id);
                pinnedIdentities[i] = GCHandle.Alloc(identityBytes, GCHandleType.Pinned);

                var info = new CF_PLACEHOLDER_CREATE_INFO();

                info.RelativeFileName = (char*)pinnedNames[i].AddrOfPinnedObject();
                info.FileIdentity = (void*)pinnedIdentities[i].AddrOfPinnedObject();
                info.FileIdentityLength = (uint)identityBytes.Length;
                info.Flags = CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_MARK_IN_SYNC;

                if (node.IsFolder)
                {
                    var folderAttrs = FileAttributes.Directory;
                    if (node.MyRights != null && !node.MyRights.MayWrite)
                        folderAttrs |= FileAttributes.ReadOnly;
                    info.FsMetadata.BasicInfo.FileAttributes = (uint)folderAttrs;
                    info.Flags |= CF_PLACEHOLDER_CREATE_FLAGS.CF_PLACEHOLDER_CREATE_FLAG_DISABLE_ON_DEMAND_POPULATION;
                }
                else
                {
                    info.FsMetadata.BasicInfo.FileAttributes = (uint)FileAttributes.Normal;
                    info.FsMetadata.FileSize = node.Size ?? 0;
                }

                if (node.Created.HasValue)
                    info.FsMetadata.BasicInfo.CreationTime = node.Created.Value.ToFileTimeUtc();

                if (node.Modified.HasValue)
                {
                    info.FsMetadata.BasicInfo.LastWriteTime = node.Modified.Value.ToFileTimeUtc();
                    info.FsMetadata.BasicInfo.ChangeTime = node.Modified.Value.ToFileTimeUtc();
                    info.FsMetadata.BasicInfo.LastAccessTime = node.Modified.Value.ToFileTimeUtc();
                }

                infos[i] = info;
            }

            uint entriesProcessed = 0;
            var hr = PInvoke.CfCreatePlaceholders(
                parentPath,
                infos.AsSpan(),
                CF_CREATE_FLAGS.CF_CREATE_FLAG_NONE,
                out entriesProcessed);

            if (hr.Failed)
            {
                // Report per-entry results before throwing
                for (int i = 0; i < children.Length; i++)
                    Log.Error($"{_logPrefix}   [{i}] {children[i].Name} (folder={children[i].IsFolder}): 0x{infos[i].Result:X8}");
                Log.Error($"{_logPrefix} CfCreatePlaceholders failed in {parentPath}: 0x{hr.Value:X8} ({entriesProcessed} processed)");
                hr.ThrowOnFailure();
            }

            Log.Info($"{_logPrefix} Created {entriesProcessed} placeholders in {parentPath}");
        }
        finally
        {
            for (int i = 0; i < children.Length; i++)
            {
                if (pinnedNames[i].IsAllocated) pinnedNames[i].Free();
                if (pinnedIdentities[i].IsAllocated) pinnedIdentities[i].Free();
            }
        }
    }

    public string GetLocalPath(FileNode node, Dictionary<string, FileNode> nodeMap)
    {
        var parts = new List<string>();
        var current = node;

        while (current != null && current.Role != "home")
        {
            parts.Add(SanitizeName(current.Name));
            if (current.ParentId != null && nodeMap.TryGetValue(current.ParentId, out var parent))
                current = parent;
            else
                break;
        }

        parts.Reverse();
        return Path.Combine(_syncRootPath, Path.Combine(parts.ToArray()));
    }
}
