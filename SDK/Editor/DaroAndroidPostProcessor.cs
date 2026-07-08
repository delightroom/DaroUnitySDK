using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Android;

namespace Daro.Editor
{
    // Android gradle post-processor — `IPostGenerateGradleAndroidProject`
    // callback that runs after Unity has generated the gradle project (and
    // EDM4U has settled at ~callbackOrder 45). Patches *only* the areas
    // EDM4U does not own.
    //
    // **Scope guardrail**: EDM4U owns artifact resolution + project repos
    // (declared in DaroDependencies.xml `<androidPackage>` + nested
    // `<repositories>`); it merges into mainTemplate.gradle / settings-
    // Template.gradle (Mode 1) or downloads AARs into Assets/Plugins/
    // Android/Repos/ (Mode 2). We do **not** post-process settings.gradle
    // repositories or unityLibrary `implementation(...)` lines — that would
    // duplicate or fight EDM4U's merge layer.
    //
    // What we patch (out-of-scope for EDM4U):
    //   1. Root build.gradle — `buildscript.repositories` (AppLovin maven
    //      URL, classpath resolve only) + `buildscript.dependencies`
    //      (so.daro:daro-plugin + AppLovinQualityServiceGradlePlugin
    //      classpaths).
    //   2. launcher/build.gradle — converts Unity's legacy
    //      `apply plugin: 'com.android.application'` line into a
    //      plugins{} DSL block that adds the Daro plugin alongside
    //      (AppLovin Quality Service requires com.android.application;
    //      Daro plugin's hooks expect plugins-DSL coordination semantics)
    //      + `minSdk` floor bump to 23.
    //   3. unityLibrary/build.gradle — `minSdk` floor bump only.
    //   4. gradle.properties — daroAppKey only (AndroidX/Jetifier are
    //      EDM4U's territory).
    //   5. proguard-user.txt — Daro keep rule (creates the file if absent).
    //
    // Plus DaroAndroidKeyFileCopier at order=51 (unityLibrary/ → launcher/
    // keyfile copy — also out-of-scope for EDM4U).
    //
    // **All inserted blocks are wrapped in marker comments** so re-running
    // on an already-patched gradle tree is a no-op. Gradle/Groovy files use
    // `//` markers; gradle.properties uses `#` markers because `//` is not a
    // comment in .properties syntax:
    //
    //     // daro-block: BEGIN <area>
    //     ... inserted content ...
    //     // daro-block: END <area>
    //
    // Where <area> ∈ {root-buildscript-repos, root-classpath,
    // launcher-plugin, gradle-properties, proguard}.
    //
    // daroAppKey: only via gradle.properties (the canonical pathway per Daro
    // guide). The earlier `unityLibrary-defaultConfig daroAppKey "..."`
    // injection has been removed — guide does not document defaultConfig as
    // a daroAppKey source, and unityLibrary-level defaultConfig wouldn't
    // reach the launcher (app) module that Daro plugin reads from anyway.
    public sealed class DaroAndroidPostProcessor : IPostGenerateGradleAndroidProject
    {
        public int callbackOrder => 50;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            Run(DaroSettingsLocator.FindOrNull(), path);
        }

        // testable seam — see plan D10-H. Tests build a fake gradle tree under
        // a temp dir and invoke this directly.
        //
        // **Path semantics**: Unity passes the *unityLibrary* subdirectory to
        // `OnPostGenerateGradleAndroidProject`, NOT the export root. Root-level
        // files (build.gradle / gradle.properties / settings.gradle) live one
        // directory up. This is a long-standing Unity quirk — verified
        // empirically against Unity 6 (6000.3.13f1) export 2026-04-29.
        internal static void Run(DaroSettings settings, string unityLibraryPath)
        {
            if (!DaroAndroidGradleContent.ShouldApply(settings))
                return;

            var rootPath = Path.GetDirectoryName(unityLibraryPath);
            if (string.IsNullOrEmpty(rootPath)) return;

            PatchRootBuildGradle(Path.Combine(rootPath, "build.gradle"));
            PatchLauncherBuildGradle(
                Path.Combine(rootPath, "launcher", "build.gradle"),
                settings);
            PatchUnityLibraryBuildGradle(
                Path.Combine(unityLibraryPath, "build.gradle"),
                settings);
            PatchGradleProperties(
                Path.Combine(rootPath, "gradle.properties"),
                settings);
            PatchProguard(Path.Combine(unityLibraryPath, "proguard-user.txt"));
        }

        // =====================================================================
        // Marker helpers
        // =====================================================================

        private const string MarkerBegin = "// daro-block: BEGIN ";
        private const string MarkerEnd   = "// daro-block: END ";
        private const string PropertiesMarkerBegin = "# daro-block: BEGIN ";
        private const string PropertiesMarkerEnd   = "# daro-block: END ";

        // Match an existing daro-block <area> region; used for idempotency
        // (skip insert if our block is already present).
        private static Regex BlockRegex(string area) =>
            new Regex(
                @"//\s*daro-block:\s*BEGIN\s+" + Regex.Escape(area) +
                @".*?//\s*daro-block:\s*END\s+" + Regex.Escape(area),
                RegexOptions.Singleline);

        private static Regex LineBlockRegex(string area) =>
            new Regex(
                @"^[ \t]*//\s*daro-block:\s*BEGIN\s+" + Regex.Escape(area) +
                @".*?^[ \t]*//\s*daro-block:\s*END\s+" + Regex.Escape(area) +
                @"[ \t]*(?:\r?\n)?",
                RegexOptions.Singleline | RegexOptions.Multiline);

        // gradle.properties historically used the regular `// daro-block`
        // marker by mistake. Match both the legacy `//` form and the correct
        // `#` form so re-running the post-processor migrates old exports.
        private static Regex PropertiesBlockRegex(string area) =>
            new Regex(
                @"^[ \t]*(?://|#)\s*daro-block:\s*BEGIN\s+" + Regex.Escape(area) +
                @".*?^[ \t]*(?://|#)\s*daro-block:\s*END\s+" + Regex.Escape(area) +
                @"[ \t]*(?:\r?\n)?",
                RegexOptions.Singleline | RegexOptions.Multiline);

        private static string Wrap(string area, string body)
        {
            var sb = new StringBuilder();
            sb.Append(MarkerBegin).Append(area).Append('\n');
            sb.Append(body);
            if (!body.EndsWith("\n")) sb.Append('\n');
            sb.Append(MarkerEnd).Append(area).Append('\n');
            return sb.ToString();
        }

        private static string WrapProperties(string area, string body)
        {
            var sb = new StringBuilder();
            sb.Append(PropertiesMarkerBegin).Append(area).Append('\n');
            sb.Append(body);
            if (!body.EndsWith("\n")) sb.Append('\n');
            sb.Append(PropertiesMarkerEnd).Append(area).Append('\n');
            return sb.ToString();
        }

        // =====================================================================
        // Root build.gradle — buildscript.repositories + buildscript.dependencies
        // =====================================================================
        //
        // Two paths depending on whether root build.gradle has a buildscript
        // block:
        //   * **Has buildscript** (Unity ≤ 2022.x typical) — surgically inject
        //     AppLovin maven URL into buildscript.repositories, classpaths
        //     into buildscript.dependencies. Two separate markers.
        //   * **No buildscript** (Unity 6 + AGP 8 default — plugins{} DSL
        //     only) — prepend a fresh `buildscript { repositories { ... }
        //     dependencies { ... } }` with both markers. Includes google() +
        //     mavenCentral() in the fresh repos so the Daro plugin (Maven
        //     Central) + AppLovin Quality Service plugin can both resolve.

        private static void PatchRootBuildGradle(string filePath)
        {
            if (!File.Exists(filePath)) return;
            var text = File.ReadAllText(filePath);

            if (HasBuildscriptBlock(text))
            {
                text = InjectBuildscriptRepo(text);
                text = InjectBuildscriptClasspaths(text);
            }
            else
            {
                text = PrependFreshBuildscript(text);
            }

            File.WriteAllText(filePath, text);
        }

        private static bool HasBuildscriptBlock(string text) =>
            new Regex(@"\bbuildscript\s*\{").IsMatch(text);

        // Insert into existing buildscript.repositories block; if a custom
        // template has buildscript{} without repositories{}, create it in place.
        // URL-deduped only inside buildscript{} — project-level repos do not
        // resolve buildscript classpaths.
        private static string InjectBuildscriptRepo(string text)
        {
            const string area = "root-buildscript-repos";
            if (BlockRegex(area).IsMatch(text)) return text;

            if (!TryFindBlockRange(text, "buildscript", out var buildscriptOpen, out var buildscriptClose))
                return text;

            var url = DaroAndroidGradleContent.AppLovinMavenUrl;
            if (text.IndexOf(url, buildscriptOpen, buildscriptClose - buildscriptOpen, System.StringComparison.Ordinal) >= 0)
                return text;

            var body = "        maven { url \"" + url + "\" }\n";
            var block = Wrap(area, body);

            var insertAt = FindNestedBlockOpen(text, "buildscript", "repositories");
            if (insertAt < 0)
            {
                var reposBlock = "    repositories {\n" + block + "    }\n";
                return text.Substring(0, buildscriptOpen) + "\n" + reposBlock + text.Substring(buildscriptOpen);
            }
            return text.Substring(0, insertAt) + "\n" + block + text.Substring(insertAt);
        }

        // Insert into existing buildscript.dependencies block; if a custom
        // template has buildscript{} without dependencies{}, create it in place.
        private static string InjectBuildscriptClasspaths(string text)
        {
            const string area = "root-classpath";
            if (BlockRegex(area).IsMatch(text)) return text;

            var sb = new StringBuilder();
            foreach (var cp in DaroAndroidGradleContent.BuildscriptClasspaths)
                sb.Append("        classpath(\"").Append(cp).Append("\")\n");
            var block = Wrap(area, sb.ToString());

            var insertAt = FindNestedBlockOpen(text, "buildscript", "dependencies");
            if (insertAt < 0)
            {
                if (!TryFindBlockRange(text, "buildscript", out var buildscriptOpen, out var buildscriptClose))
                    return text;

                var depsBlock = "    dependencies {\n" + block + "    }\n";
                return text.Substring(0, buildscriptClose) + "\n" + depsBlock + text.Substring(buildscriptClose);
            }
            return text.Substring(0, insertAt) + "\n" + block + text.Substring(insertAt);
        }

        // No-buildscript path: prepend a complete buildscript block with
        // repositories + dependencies + both markers. Repos include google()
        // + mavenCentral() (for Daro plugin) + AppLovin maven (for Quality
        // Service plugin). Idempotent via marker check on either area.
        private static string PrependFreshBuildscript(string text)
        {
            if (BlockRegex("root-buildscript-repos").IsMatch(text)) return text;
            if (BlockRegex("root-classpath").IsMatch(text)) return text;

            var repos = new StringBuilder();
            repos.Append("        google()\n");
            repos.Append("        mavenCentral()\n");
            repos.Append("        maven { url \"")
                 .Append(DaroAndroidGradleContent.AppLovinMavenUrl)
                 .Append("\" }\n");
            var reposBlock = Wrap("root-buildscript-repos", repos.ToString());

            var deps = new StringBuilder();
            foreach (var cp in DaroAndroidGradleContent.BuildscriptClasspaths)
                deps.Append("        classpath(\"").Append(cp).Append("\")\n");
            var depsBlock = Wrap("root-classpath", deps.ToString());

            var prepend = new StringBuilder();
            prepend.Append("buildscript {\n");
            prepend.Append("    repositories {\n").Append(reposBlock).Append("    }\n");
            prepend.Append("    dependencies {\n").Append(depsBlock).Append("    }\n");
            prepend.Append("}\n");

            return prepend.ToString() + text;
        }

        // Returns the index *after* the inner block's opening brace
        // (`<inner> {`) when it appears inside the first occurrence of
        // `<outer> { ... }`. Returns -1 if either is missing.
        //
        // Implemented as a two-step search instead of a single nested
        // regex: `[^}]*?` between `outer {` and `inner {` fails when the
        // outer block contains earlier `}` (e.g. a sibling `repositories
        // { ... }` before `dependencies { ... }`), causing the previous
        // implementation to silently fall through to its fallback path.
        // Two-step search trusts that Unity's root build.gradle layout has
        // exactly one `buildscript {` and that the next `<inner> {` after
        // it is the one we want.
        private static int FindNestedBlockOpen(string text, string outer, string inner)
        {
            var innerStart = FindNestedBlockStart(text, outer, inner);
            if (innerStart < 0) return -1;

            var rxInner = new Regex(@"\b" + Regex.Escape(inner) + @"\s*\{");
            var mInner = rxInner.Match(text, innerStart);
            return mInner.Success ? mInner.Index + mInner.Length : -1;
        }

        private static int FindNestedBlockStart(string text, string outer, string inner)
        {
            if (!TryFindBlockRange(text, outer, out var outerOpen, out var outerClose))
                return -1;

            var rxInner = new Regex(@"\b" + Regex.Escape(inner) + @"\s*\{");
            var mInner = rxInner.Match(text, outerOpen);
            if (!mInner.Success || mInner.Index >= outerClose) return -1;

            return mInner.Index;
        }

        private static bool TryFindBlockRange(string text, string blockName, out int openIndex, out int closeIndex)
        {
            openIndex = -1;
            closeIndex = -1;

            var rx = new Regex(@"\b" + Regex.Escape(blockName) + @"\s*\{");
            var match = rx.Match(text);
            if (!match.Success) return false;

            openIndex = match.Index + match.Length;
            var depth = 1;
            for (var i = openIndex; i < text.Length; i++)
            {
                if (text[i] == '{')
                {
                    depth++;
                    continue;
                }

                if (text[i] != '}') continue;

                depth--;
                if (depth == 0)
                {
                    closeIndex = i;
                    return true;
                }
            }

            openIndex = -1;
            return false;
        }

        // =====================================================================
        // launcher/build.gradle — Daro plugin apply + minSdk floor bump
        // =====================================================================
        //
        // The Daro plugin (`so.daro.m` / `so.daro.a`) transitively applies
        // `applovin-quality-service`, which only supports
        // `com.android.application` projects. Unity's `launcher/` module is
        // the application module (`com.android.application`); `unityLibrary/`
        // is a library (`com.android.library`). So plugin apply MUST be on
        // launcher, never unityLibrary. Verified empirically against Unity 6
        // 6000.3.13f1 export 2026-04-29 — applying to unityLibrary fails with
        // "AppLovin Quality Service Plugin can only be applied to Android
        // Application projects".

        private static void PatchLauncherBuildGradle(string filePath, DaroSettings settings)
        {
            if (!File.Exists(filePath)) return;
            var text = File.ReadAllText(filePath);

            text = InjectLauncherPluginApply(text, settings);
            text = BumpMinSdk(text);

            File.WriteAllText(filePath, text);
        }

        // Replaces Unity's legacy `apply plugin: 'com.android.application'`
        // line with a modern `plugins {}` DSL block that applies BOTH
        // com.android.application AND the Daro plugin. This is the pattern
        // the Daro plugin's apply-time hooks are designed for.
        //
        // Why not just `apply plugin: 'so.daro.m'` somewhere in the file?
        // Verified empirically (Unity 6 6000.3.13f1, AGP 8.10): both
        //   (a) directly after `apply plugin: 'com.android.application'`
        //   (b) at file end (after the `android { }` block)
        // produce "[SafeDK-ERROR] Android variants not detected" plus
        // "Daro 앱 키를 찾을 수 없습니다 (variant: debug)". The Daro plugin's
        // hook timing relies on plugins-DSL coordination: AGP applies first
        // and registers its variant configuration via plugins-DSL's
        // ordered apply mechanism, then so.daro.m applies and registers its
        // afterEvaluate hooks atop AGP's already-installed pipeline.
        //
        // `id 'com.android.application'` here omits the version because
        // Unity's root build.gradle declares
        //   `id 'com.android.application' version '8.10.0' apply false`
        // which provides the version. `id 'so.daro.m'` resolves via the
        // root buildscript classpath (so.daro:daro-plugin) we injected.
        private static string InjectLauncherPluginApply(string text, DaroSettings settings)
        {
            const string area = "launcher-plugin";
            if (BlockRegex(area).IsMatch(text)) return text;

            var pluginId = DaroAndroidGradleContent.GetPluginId(settings.mediation);

            // Match Unity's `apply plugin: 'com.android.application'` line
            // (with single OR double quotes, optional whitespace).
            var rx = new Regex(
                @"apply\s+plugin\s*:\s*['""]com\.android\.application['""]\s*\r?\n");
            var m = rx.Match(text);
            if (!m.Success)
            {
                // Defensive: launcher/build.gradle without an
                // `apply plugin: 'com.android.application'` line is unexpected.
                // Leave file untouched rather than guess.
                return text;
            }

            var body = new StringBuilder();
            body.Append("plugins {\n");
            body.Append("    id 'com.android.application'\n");
            body.Append("    id '").Append(pluginId).Append("'\n");
            body.Append("}\n");
            var block = Wrap(area, body.ToString());

            // Replace the matched legacy apply line with our wrapped plugins{} block.
            return text.Substring(0, m.Index) + block + text.Substring(m.Index + m.Length);
        }

        // =====================================================================
        // unityLibrary/build.gradle — minSdk floor bump only
        // =====================================================================
        //
        // Daro plugin apply moved to launcher/. This module only needs minSdk
        // bumping in case the consumer's Unity floor is below Daro's 23.

        private static void PatchUnityLibraryBuildGradle(string filePath, DaroSettings settings)
        {
            if (!File.Exists(filePath)) return;
            var text = File.ReadAllText(filePath);

            text = BumpMinSdk(text);

            // No InjectDependencies — `implementation("so.daro:daro-m:...")`
            // is EDM4U's responsibility (declared in DaroDependencies.xml).
            // No plugin apply — see PatchLauncherBuildGradle (so.daro.m
            // requires com.android.application).

            File.WriteAllText(filePath, text);
        }

        private static string BumpMinSdk(string text)
        {
            // Matches both legacy `minSdkVersion 21` (Groovy DSL, Unity ≤ 2022.x)
            // and AGP 8 short form `minSdk 21` (Unity 6 default), plus the
            // `(21)` paren variant. Captures the keyword so the rewrite
            // preserves the same syntax the file already uses.
            var rx = new Regex(@"\b(minSdk(?:Version)?)\s*\(?\s*(\d+)\s*\)?");
            return rx.Replace(text, m =>
            {
                if (int.TryParse(m.Groups[2].Value, out var current) &&
                    current < DaroAndroidGradleContent.MinSdk)
                {
                    return m.Groups[1].Value + " " + DaroAndroidGradleContent.MinSdk;
                }
                return m.Value;
            });
        }

        // =====================================================================
        // gradle.properties — daroAppKey only (AndroidX / Jetifier are EDM4U-owned)
        // =====================================================================

        private static void PatchGradleProperties(string filePath, DaroSettings settings)
        {
            if (!File.Exists(filePath)) return;
            var text = File.ReadAllText(filePath);
            const string area = "gradle-properties";
            var blockRegex = PropertiesBlockRegex(area);
            var hadBlock = blockRegex.IsMatch(text);
            var textWithoutBlock = blockRegex.Replace(text, string.Empty);

            var props = DaroAndroidGradleContent.GetGradleProperties(settings);
            var existingKeys = ParsePropertyKeys(textWithoutBlock);

            // Filter to keys that are NOT already present anywhere in the file
            // (preserve consumer's existing values verbatim).
            var toWrite = new List<KeyValuePair<string, string>>();
            foreach (var kv in props)
            {
                if (!existingKeys.Contains(kv.Key))
                    toWrite.Add(kv);
            }

            if (toWrite.Count == 0)
            {
                if (hadBlock)
                    File.WriteAllText(filePath, textWithoutBlock);
                return;
            }

            var sb = new StringBuilder();
            foreach (var kv in toWrite)
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\n');

            var block = WrapProperties(area, sb.ToString());
            text = textWithoutBlock;
            if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";
            text += block;

            File.WriteAllText(filePath, text);
        }

        private static HashSet<string> ParsePropertyKeys(string propertiesText)
        {
            var keys = new HashSet<string>();
            foreach (var rawLine in propertiesText.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#") || line.StartsWith("!"))
                    continue;
                // Skip our own marker lines so they're not parsed as property lines.
                if (line.StartsWith("//")) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                keys.Add(line.Substring(0, eq).Trim());
            }
            return keys;
        }

        // settings.gradle is intentionally NOT patched here. The sub-network
        // maven repos are declared inside DaroDependencies.xml's
        // <androidPackage> <repositories> block — EDM4U owns that merge per
        // its standard schema. Layering our own post-process on top would
        // duplicate or fight EDM4U's output.

        // =====================================================================
        // proguard-user.txt — sync keep rules
        // =====================================================================

        private static void PatchProguard(string filePath)
        {
            // create-if-missing: Unity 2023.1+ exports may omit
            // proguard-user.txt by default (no `useCustomProguardFile`
            // toggle to opt in). We seed it ourselves so the keep rule has
            // a stable home in either Unity version.
            var text = File.Exists(filePath) ? File.ReadAllText(filePath) : string.Empty;
            const string area = "proguard";

            var body = DaroAndroidGradleContent.ProguardKeepRule + "\n";
            var block = Wrap(area, body);
            var blockRegex = LineBlockRegex(area);

            if (blockRegex.IsMatch(text))
            {
                var updated = blockRegex.Replace(text, block, 1);
                if (updated != text)
                    File.WriteAllText(filePath, updated);
                return;
            }

            if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";

            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(filePath, text + block);
        }
    }
}
