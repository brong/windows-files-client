using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;
using Windows.Win32.Storage.FileSystem;
using FileNodeClient.Logging;
using FileNodeClient.Jmap.Models;

namespace FileNodeClient.Windows;

internal class PlaceholderManager
{
    // Windows-invalid filename characters and their Unicode look-alike replacements.
    // Fullwidth forms are visually similar and commonly used by cloud sync clients
    // (OneDrive, rclone, etc.) for lossless bidirectional filename mapping.
    private static readonly (char Invalid, char Safe)[] CharMap =
    [
        ('\\', '\uFF3C'), // FULLWIDTH REVERSE SOLIDUS
        ('/',  '\u2215'), // DIVISION SLASH
        (':',  '\uA789'), // MODIFIER LETTER COLON
        ('*',  '\uFF0A'), // FULLWIDTH ASTERISK
        ('?',  '\uFF1F'), // FULLWIDTH QUESTION MARK
        ('"',  '\uFF02'), // FULLWIDTH QUOTATION MARK
        ('<',  '\uFF1C'), // FULLWIDTH LESS-THAN SIGN
        ('>',  '\uFF1E'), // FULLWIDTH GREATER-THAN SIGN
        ('|',  '\uFF5C'), // FULLWIDTH VERTICAL LINE
    ];

    private static readonly char[] InvalidChars = CharMap.Select(m => m.Invalid).ToArray();
    private static readonly char[] SafeChars = CharMap.Select(m => m.Safe).ToArray();

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

    // Zero-width no-break space — invisible prefix/suffix for reserved name
    // and trailing dot/space round-tripping.
    private const char Marker = '\uFEFF';

    // Injected case/sanitization-collision disambiguator: the invisible Marker followed by
    // "(N)". Matched here so DesanitizeName can strip it without touching a user's literal
    // " (2)" in a real filename (which has no Marker).
    private static readonly Regex CollisionSuffixPattern = new(Marker + @"\(\d+\)", RegexOptions.Compiled);

    /// <summary>
    /// Replace characters that are invalid in Windows filenames with visually
    /// similar Unicode look-alikes (fullwidth forms). This mapping is lossless
    /// and reversible via <see cref="DesanitizeName"/>.
    /// Reserved names (CON, etc.) and trailing dots/spaces are preserved using
    /// a zero-width marker character.
    /// </summary>
    internal static string SanitizeName(string name)
    {
        // JMAP requires Net-Unicode (NFC). NTFS is byte-preserving, so normalize
        // incoming server names to NFC up front — local paths and the comparisons
        // built on them (echo suppression, reverse lookups) must use one canonical
        // form, or a server NFD name and an NFC name for the same file diverge.
        var result = name.IsNormalized(NormalizationForm.FormC)
            ? name
            : name.Normalize(NormalizationForm.FormC);

        // Replace invalid chars with Unicode look-alikes
        if (result.IndexOfAny(InvalidChars) >= 0)
        {
            var chars = result.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                int idx = Array.IndexOf(InvalidChars, chars[i]);
                if (idx >= 0)
                    chars[i] = SafeChars[idx];
            }
            result = new string(chars);
        }

        // Preserve trailing spaces/dots with an invisible marker so
        // DesanitizeName can restore them. Windows silently strips these.
        if (result.Length > 0 && (result[^1] == ' ' || result[^1] == '.'))
            result += Marker;

        // Prefix reserved names with invisible marker to avoid device name
        // collisions and path traversal, while keeping the name readable.
        if (result.Length == 0 || ReservedNames.Contains(result))
            result = Marker + result;

        return result;
    }

    /// <summary>
    /// Disambiguate a sanitized name that collides — case-insensitively, or after
    /// sanitization — with a sibling on a case-insensitive local filesystem. Injects an
    /// invisible Marker-tagged "(N)" before the extension (e.g. "report﻿(2).txt", shown as
    /// "report(2).txt"), so the local name is unique yet reversible by <see cref="DesanitizeName"/>
    /// — without corrupting a user's literal " (2)". <paramref name="isTaken"/> reports
    /// whether a candidate local name is already used by a different sibling. Strictly safer
    /// than silently skipping the collision, which would hide a file the user has (DESIGN §14, #41).
    /// </summary>
    internal static string MakeUniqueName(string sanitizedName, Func<string, bool> isTaken)
    {
        if (!isTaken(sanitizedName))
            return sanitizedName;

        var ext = Path.GetExtension(sanitizedName);
        var stem = sanitizedName[..^ext.Length];
        for (int n = 2; ; n++)
        {
            var candidate = $"{stem}{Marker}({n}){ext}";
            if (!isTaken(candidate))
                return candidate;
        }
    }

    /// <summary>
    /// Reverse of <see cref="SanitizeName"/>: restore Unicode look-alikes back
    /// to their original characters for sending to the server.
    /// Call this on local filenames before using them in JMAP calls.
    /// </summary>
    internal static string DesanitizeName(string localName)
    {
        var result = localName;

        // Strip any injected case-collision disambiguator(s) first, so the lone prefix/suffix
        // Marker handling below operates on the genuine name. The pattern is Marker + "(N)".
        if (result.IndexOf(Marker) >= 0)
            result = CollisionSuffixPattern.Replace(result, "");

        // Remove marker prefix (reserved name protection)
        if (result.Length > 0 && result[0] == Marker)
            result = result[1..];

        // Remove marker suffix (trailing dot/space preservation)
        if (result.Length > 0 && result[^1] == Marker)
            result = result[..^1];

        // Restore Unicode look-alikes to original characters
        if (result.IndexOfAny(SafeChars) >= 0)
        {
            var chars = result.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
            {
                int idx = Array.IndexOf(SafeChars, chars[i]);
                if (idx >= 0)
                    chars[i] = InvalidChars[idx];
            }
            result = new string(chars);
        }

        // JMAP requires Net-Unicode (NFC). NTFS hands back whatever bytes were
        // written and macOS/Linux peers may have produced NFD, so normalize every
        // name to NFC before it goes to the server — otherwise a round-tripped name
        // is binary-distinct and the server creates a duplicate (DESIGN §14, #40).
        return result.IsNormalized(NormalizationForm.FormC)
            ? result
            : result.Normalize(NormalizationForm.FormC);
    }

    /// <param name="localNameByNodeId">
    /// Optional precomputed local file names per node ID (case-collision disambiguated by
    /// the caller). When a node is present, its mapped name is used verbatim; otherwise the
    /// name falls back to <see cref="SanitizeName"/> of the node's server name.
    /// </param>
    public unsafe void CreatePlaceholders(string parentPath, FileNode[] children,
        IReadOnlyDictionary<string, string>? localNameByNodeId = null)
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
                var sanitized = localNameByNodeId != null && localNameByNodeId.TryGetValue(node.Id, out var mapped)
                    ? mapped
                    : SanitizeName(node.Name);
                var nameChars = new char[sanitized.Length + 1];
                sanitized.CopyTo(nameChars);
                nameChars[sanitized.Length] = '\0';
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
