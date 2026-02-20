using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;
using FilesClient.Jmap.Models;

namespace FilesClient.Windows;

internal class PlaceholderManager
{
    private static readonly char[] InvalidChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    private readonly string _syncRootPath;
    private readonly string _logPrefix;

    private void Log(string msg) => Console.WriteLine($"{_logPrefix} {msg}");
    private void LogError(string msg) => Console.Error.WriteLine($"{_logPrefix} {msg}");

    public PlaceholderManager(string syncRootPath, string logPrefix)
    {
        _syncRootPath = syncRootPath;
        _logPrefix = logPrefix;
    }

    /// <summary>
    /// Replace characters that are invalid in Windows filenames with underscore.
    /// </summary>
    internal static string SanitizeName(string name)
    {
        if (name.IndexOfAny(InvalidChars) < 0)
            return name;

        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(InvalidChars, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars).TrimEnd(' ', '.');
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
                    LogError($"  [{i}] {children[i].Name} (folder={children[i].IsFolder}): 0x{infos[i].Result:X8}");
                LogError($"CfCreatePlaceholders failed in {parentPath}: 0x{hr.Value:X8} ({entriesProcessed} processed)");
                hr.ThrowOnFailure();
            }

            Log($"Created {entriesProcessed} placeholders in {parentPath}");
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
