import Foundation

/// Utilities for normalising filenames before presenting them to the OS or sending to the server.
public enum FilenameUtils {

    // MARK: - Unicode normalization

    /// NFC-normalises a string (Canonical Decomposition, then Canonical Composition).
    ///
    /// JMAP names must be Net-Unicode (RFC 5198), which requires NFC. APFS/HFS+ may
    /// store names in NFD-like form, so this must be applied before any server API call.
    public static func nfc(_ name: String) -> String {
        (name as NSString).precomposedStringWithCanonicalMapping
    }

    // MARK: - Sanitization

    /// Prepares a server-supplied name for presentation to macOS/iOS.
    ///
    /// - NFC-normalises (defensive; server should already send NFC per spec).
    /// - Replaces characters that are forbidden or mishandled by macOS filesystems.
    public static func sanitize(_ name: String) -> String {
        nfc(name)
            .replacingOccurrences(of: "/", with: "\u{2215}")  // DIVISION SLASH
            .replacingOccurrences(of: ":", with: "\u{A789}")  // MODIFIER LETTER COLON
    }

    /// Reverses `sanitize` and NFC-normalises, producing a name safe to send to the server.
    ///
    /// Call this on any filename received from macOS (FileProvider `item.filename`) before
    /// including it in a JMAP API request. APFS returns filenames in NFD; the server
    /// expects NFC (Net-Unicode, RFC 5198).
    public static func desanitize(_ name: String) -> String {
        nfc(
            name
                .replacingOccurrences(of: "\u{2215}", with: "/")   // DIVISION SLASH → /
                .replacingOccurrences(of: "\u{A789}", with: ":")   // MODIFIER LETTER COLON → :
        )
    }

    // MARK: - Case-collision deduplication

    /// Given a parallel array of sibling names, returns display-name overrides for any
    /// entries that collide when compared case- and Unicode-form-insensitively.
    ///
    /// Both `"foo.txt"` / `"FOO.txt"` (case variants) and `"café.txt"` (NFC) /
    /// `"cafe\u{0301}.txt"` (NFD) are treated as collisions.
    ///
    /// The lexicographically-first actual name is left unchanged; the others get
    /// " (N)" inserted before the file extension so both appear in Finder.
    ///
    /// Returns: parallel array of optional overrides (nil = use the real name).
    public static func caseCollisionSuffixes(for names: [String]) -> [String?] {
        var groups: [String: [Int]] = [:]
        for (i, name) in names.enumerated() {
            // NFC-normalise the key so Unicode-form variants (NFC vs NFD) group together,
            // then lowercase so case variants group together too.
            groups[nfc(name).lowercased(), default: []].append(i)
        }

        var overrides = [String?](repeating: nil, count: names.count)
        for indices in groups.values where indices.count > 1 {
            let sorted = indices.sorted { names[$0] < names[$1] }
            for (rank, idx) in sorted.dropFirst().enumerated() {
                overrides[idx] = insertCaseSuffix(names[idx], n: rank + 2)
            }
        }
        return overrides
    }

    static func insertCaseSuffix(_ name: String, n: Int) -> String {
        guard let dot = name.lastIndex(of: "."), dot != name.startIndex else {
            return "\(name) (\(n))"
        }
        let base = String(name[..<dot])
        let ext  = String(name[dot...])
        return "\(base) (\(n))\(ext)"
    }
}
