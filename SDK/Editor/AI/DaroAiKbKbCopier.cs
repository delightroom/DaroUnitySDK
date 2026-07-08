using System.IO;
using UnityEditor;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using Daro.Internal;

namespace Daro.Editor
{
    // Mirrors `<package>/Documentation~/` into `<project>/.daro/integration-kb/`
    // so directive payloads can reference a stable consumer-project-local
    // path regardless of how the SDK is installed (embedded UPM, OpenUPM
    // registry, Library/PackageCache, etc.).
    //
    // Ownership marker: a sentinel file (`.daro-owned`) inside the dest
    // directory marks it as vendor-owned. Clean refuses to delete a
    // directory without the sentinel so a user-authored directory at the
    // same path stays untouched.
    //
    // Source resolution order:
    //   1. UnityEditor.PackageManager.PackageInfo.FindForAssembly — works
    //      for embedded / registry / cache installs.
    //   2. Fallback: AssetDatabase lookup of `Daro.Editor` asmdef → SDK
    //      root. Covers the SDK developer's own workspace where the
    //      package sits at the project root rather than under `Packages/`.
    //
    // Apply returns the disposition so Bootstrap / Manager UI / Validator
    // can react (NoOp / Updated / UserOwnedSkipped / SourceUnavailable).
    internal static class DaroAiKbKbCopier
    {
        internal enum ApplyResult
        {
            // Source `Documentation~/` couldn't be located. SDK install
            // path lookup failed — Bootstrap surfaces a Warn.
            SourceUnavailable,
            // `.daro/integration-kb/` did not exist; created and populated.
            Created,
            // Existed and was vendor-owned; content differed; refreshed.
            Updated,
            // Existed, vendor-owned, content matches source → no write.
            NoOp,
            // Existed but is NOT vendor-owned (no sentinel) — refused to
            // overwrite. Validator Warn surfaces it.
            UserOwnedSkipped,
        }

        internal static ApplyResult Apply()
            => ApplyAt(ResolveSourceDir(), DaroAiKbPaths.KbDirAbsolute(DaroProjectRoot.Path));

        internal static ApplyResult ApplyAt(string source, string dest)
        {
            if (string.IsNullOrEmpty(source) || !Directory.Exists(source))
            {
                DaroLog.Warn("Editor", "[AI KB] KB source `Documentation~/` not found — SDK install path unresolved.");
                return ApplyResult.SourceUnavailable;
            }

            if (Directory.Exists(dest))
            {
                if (!IsOwnedDir(dest))
                {
                    DaroLog.Warn("Editor", $"[AI KB] KB copy user-owned skip → {dest} (no ownership sentinel)");
                    return ApplyResult.UserOwnedSkipped;
                }

                if (ContentsMatch(source, dest))
                    return ApplyResult.NoOp;

                // Vendor-owned but stale — wipe and re-copy. Safe because
                // sentinel confirms we own this directory.
                Directory.Delete(dest, recursive: true);
                CopyTree(source, dest);
                WriteSentinel(dest);
                DaroLog.Info("Editor", $"[AI KB] Updated → {dest}");
                return ApplyResult.Updated;
            }

            CopyTree(source, dest);
            WriteSentinel(dest);
            DaroLog.Info("Editor", $"[AI KB] Created → {dest}");
            return ApplyResult.Created;
        }

        internal static bool Clean()
            => CleanAt(DaroAiKbPaths.KbDirAbsolute(DaroProjectRoot.Path));

        internal static bool CleanAt(string dest)
        {
            if (!Directory.Exists(dest)) return false;
            if (!IsOwnedDir(dest)) return false;

            Directory.Delete(dest, recursive: true);
            DaroLog.Info("Editor", $"[AI KB] Clean → {dest}");
            return true;
        }

        // True when the KB copy directory exists and carries the
        // vendor-ownership sentinel. Used by Validator / UI status.
        internal static bool IsOwned()
            => IsOwnedAt(DaroAiKbPaths.KbDirAbsolute(DaroProjectRoot.Path));

        internal static bool IsOwnedAt(string dest)
            => Directory.Exists(dest) && IsOwnedDir(dest);

        // True when source `Documentation~/` and dest `.daro/integration-kb/`
        // have byte-identical content for every markdown file.
        internal static bool IsUpToDate()
            => IsUpToDateAt(ResolveSourceDir(), DaroAiKbPaths.KbDirAbsolute(DaroProjectRoot.Path));

        internal static bool IsUpToDateAt(string source, string dest)
        {
            if (string.IsNullOrEmpty(source) || !Directory.Exists(source)) return false;
            if (!Directory.Exists(dest)) return false;
            return ContentsMatch(source, dest);
        }

        // === Internals ========================================================

        private static string ResolveSourceDir()
        {
            // (1) UPM PackageInfo lookup — works for all consumer install
            // methods (embedded / registry / cache).
            var asm = typeof(DaroAiKbKbCopier).Assembly;
            var pkg = PackageInfo.FindForAssembly(asm);
            if (pkg != null && !string.IsNullOrEmpty(pkg.resolvedPath))
            {
                var docs = Path.Combine(pkg.resolvedPath, "Documentation~");
                if (Directory.Exists(docs)) return docs;
            }

            // (2) SDK developer workspace fallback — find `Daro.Editor`
            // asmdef and ascend to SDK root.
            var guids = AssetDatabase.FindAssets("Daro.Editor t:AssemblyDefinitionAsset");
            foreach (var guid in guids)
            {
                var asmdefRel = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(asmdefRel)) continue;
                // asmdefRel = e.g. "SDK/Editor/Daro.Editor.asmdef" (project-root-relative)
                var asmdefAbs = Path.Combine(DaroProjectRoot.Path, asmdefRel);
                var editorDir = Path.GetDirectoryName(asmdefAbs);
                if (string.IsNullOrEmpty(editorDir)) continue;
                var sdkRoot = Path.GetDirectoryName(editorDir);
                if (string.IsNullOrEmpty(sdkRoot)) continue;
                var docs = Path.Combine(sdkRoot, "Documentation~");
                if (Directory.Exists(docs)) return docs;
            }

            return null;
        }

        private static bool IsOwnedDir(string dir)
            => File.Exists(Path.Combine(dir, DaroAiKbPaths.KbSentinelFileName));

        private static void WriteSentinel(string dir)
            => File.WriteAllText(Path.Combine(dir, DaroAiKbPaths.KbSentinelFileName),
                "daro-vendor-owned\n");

        // Recursive directory copy of all *.md files (ad-formats/ included).
        // Other files (e.g. *.meta) are skipped — they're Unity AssetDatabase
        // metadata not relevant to the KB content. Sentinel file is written
        // separately by Apply (not source-derived).
        private static void CopyTree(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var srcFile in Directory.GetFiles(source, "*.md", SearchOption.AllDirectories))
            {
                var rel = GetRelativePath(source, srcFile);
                var destFile = Path.Combine(dest, rel);
                var destDir = Path.GetDirectoryName(destFile);
                if (!string.IsNullOrEmpty(destDir) && !Directory.Exists(destDir))
                    Directory.CreateDirectory(destDir);
                File.Copy(srcFile, destFile, overwrite: true);
            }
        }

        // True iff every *.md file under source has a byte-identical
        // counterpart under dest. Sentinel file ignored.
        private static bool ContentsMatch(string source, string dest)
        {
            foreach (var srcFile in Directory.GetFiles(source, "*.md", SearchOption.AllDirectories))
            {
                var rel = GetRelativePath(source, srcFile);
                var destFile = Path.Combine(dest, rel);
                if (!File.Exists(destFile)) return false;
                if (File.ReadAllText(srcFile) != File.ReadAllText(destFile)) return false;
            }
            // Also verify dest doesn't have extra (stale) markdown files —
            // a previous SDK version may have shipped a file we since
            // removed.
            foreach (var destFile in Directory.GetFiles(dest, "*.md", SearchOption.AllDirectories))
            {
                var rel = GetRelativePath(dest, destFile);
                var srcFile = Path.Combine(source, rel);
                if (!File.Exists(srcFile)) return false;
            }
            return true;
        }

        // Unity's .NET Standard 2.0 profile doesn't expose Path.GetRelativePath,
        // so roll our own. Both paths are assumed to be absolute under the same
        // os-canonical form (callers always pass directory-rooted absolute paths).
        private static string GetRelativePath(string root, string fullPath)
        {
            var rootNorm = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(rootNorm, System.StringComparison.Ordinal))
                return fullPath.Substring(rootNorm.Length);
            return fullPath;
        }
    }
}
