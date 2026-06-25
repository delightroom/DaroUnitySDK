using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Daro.Internal;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Callbacks;
using UnityEditor.iOS.Xcode;
using UnityEditor.iOS.Xcode.Extensions;
// `UnityEditor.iOS.Xcode` ships with the iOS Build Support module — usable from
// any build target as long as the module is installed. The previous `#if UNITY_IOS`
// guards made ApplyPlistChanges a no-op when EditMode tests ran with the active
// target on Standalone/Android — they're dropped intentionally.

namespace Daro.Editor
{
    // iOS post-build hook (callbackOrder = 50). EDM4U IOSResolver runs at ~45,
    // so by the time we mutate the project tree, all Pod-driven changes are
    // settled. Two responsibilities (sketch §8):
    //
    //   1. Info.plist additive injection — DaroAppKey / GADApplicationIdentifier
    //      (when set) / NSUserTrackingUsageDescription / SKAdNetworkItems
    //      (merged with any existing list) / NSAppTransportSecurity.
    //      All injections are *additive only* — existing non-empty consumer values
    //      survive untouched.
    //
    //   2. PBXProject — copy ios-daro-key.txt into Xcode project root and add it
    //      to the main app target's Copy Bundle Resources phase. Daro plugin reads
    //      the file from Bundle.main at runtime; UnityFramework target wouldn't be
    //      visible to the plugin code.
    //
    // The plist branch is automatically tested via ApplyPlistChanges seam.
    // The PBX branch is exercised by manual smoke during sprint exit (real
    // Xcode export fixture is large and Unity-version-fragile).
    public static class DaroIosPostProcessor
    {
        [PostProcessBuild(50)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;

            var settings = DaroSettingsLocator.FindOrNull();
            if (settings == null) return; // validator already blocked

            var plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
            ApplyPlistChanges(settings, plistPath, LoadSkAdNetworkIds());
            ApplyPbxChanges(settings, pathToBuiltProject);
            EnableObjCExceptionsOnUnityFramework(pathToBuiltProject);
        }

        // DaroUnityBridge.mm 의 DestroyAll 안 per-helper @try/@catch (shutdown
        // best-effort teardown isolation) 를 위해 ObjC exceptions 활성화. Unity
        // 가 export 한 Xcode project 의 default 가 NO 라서 안 켜면 컴파일 실패.
        // Daro plugins (Plugins/iOS/*) 는 UnityFramework target 에 attach 되므로
        // main Unity-iPhone target 은 default(NO) 유지.
        private static void EnableObjCExceptionsOnUnityFramework(string pathToBuiltProject)
        {
            var pbxPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var pbx = new PBXProject();
            pbx.ReadFromFile(pbxPath);

            var unityFrameworkGuid = pbx.GetUnityFrameworkTargetGuid();
            pbx.SetBuildProperty(unityFrameworkGuid, "GCC_ENABLE_OBJC_EXCEPTIONS", "YES");

            // ILRD: DaroUnityRevenueToken.swift (CryptoKit) lives in Plugins/iOS
            // and is attached to UnityFramework. Set SWIFT_VERSION so it compiles;
            // it is C-callable via @_cdecl, so no bridging header is needed.
            pbx.SetBuildProperty(unityFrameworkGuid, "SWIFT_VERSION", "5.0");

            pbx.WriteToFile(pbxPath);
        }

        // -- Plist seam (testable) -------------------------------------------

        internal static void ApplyPlistChanges(
            DaroSettings settings,
            string plistFullPath,
            IEnumerable<string> skAdNetworkIds)
        {
            if (settings == null) return;

            var plist = new PlistDocument();
            plist.ReadFromFile(plistFullPath);
            var root = plist.root;

            // DaroAppKey — always inject when set.
            if (!string.IsNullOrEmpty(settings.iosDaroAppKey))
                root.SetString("DaroAppKey", settings.iosDaroAppKey);

            // GADApplicationIdentifier — Daro 가이드 명시 + Apple/Google 강제
            // 요구. daro-m 의 transitive `applovin-mediation:google-adapter`
            // 가 GoogleMobileAds framework 를 링크하므로 이 키 없으면 앱
            // 실행 직후 크래시. v1 MAX variant 도 적용 (이전 v2 deferral
            // 폐기 — 가이드 + Apple 동작 확인 후 결정).
            if (!string.IsNullOrEmpty(settings.iosAdMobAppId))
                root.SetString("GADApplicationIdentifier", settings.iosAdMobAppId);

            // ATT description — inject when set. validator gates empty.
            if (!string.IsNullOrEmpty(settings.attPromptDescription))
                root.SetString("NSUserTrackingUsageDescription", settings.attPromptDescription);

            // SKAdNetworkItems — merge-additive (dedupe by SKAdNetworkIdentifier).
            MergeSkAdNetworkItems(root, skAdNetworkIds);

            // NSAppTransportSecurity.NSAllowsArbitraryLoads — additive only-if-absent.
            EnsureAtsAllowArbitraryLoads(root);

            plist.WriteToFile(plistFullPath);
        }

        private static void MergeSkAdNetworkItems(PlistElementDict root, IEnumerable<string> idsToAdd)
        {
            if (idsToAdd == null) return;
            var idsList = idsToAdd.Where(id => !string.IsNullOrEmpty(id)).Distinct().ToList();
            if (idsList.Count == 0 && !root.values.ContainsKey("SKAdNetworkItems")) return;

            var array = root.values.ContainsKey("SKAdNetworkItems")
                ? root.values["SKAdNetworkItems"].AsArray()
                : root.CreateArray("SKAdNetworkItems");

            // Existing identifiers — collect to dedupe.
            var existing = new HashSet<string>();
            foreach (var item in array.values)
            {
                var dict = item.AsDict();
                if (dict.values.TryGetValue("SKAdNetworkIdentifier", out var idVal))
                    existing.Add(idVal.AsString());
            }

            foreach (var id in idsList)
            {
                if (existing.Contains(id)) continue;
                array.AddDict().SetString("SKAdNetworkIdentifier", id);
                existing.Add(id);
            }
        }

        private static void EnsureAtsAllowArbitraryLoads(PlistElementDict root)
        {
            var ats = root.values.ContainsKey("NSAppTransportSecurity")
                ? root.values["NSAppTransportSecurity"].AsDict()
                : root.CreateDict("NSAppTransportSecurity");

            // Additive only-if-absent — preserve consumer's explicit setting.
            if (!ats.values.ContainsKey("NSAllowsArbitraryLoads"))
                ats.SetBoolean("NSAllowsArbitraryLoads", true);
        }

        private static void ApplyPbxChanges(DaroSettings settings, string pathToBuiltProject)
        {
            if (settings.iosKeyFile == null) return;

            var srcAssetPath = AssetDatabase.GetAssetPath(settings.iosKeyFile);
            var srcAbsolute = Path.GetFullPath(srcAssetPath);
            if (!File.Exists(srcAbsolute))
            {
                throw new BuildFailedException(
                    $"[Daro] ios-daro-key.txt source missing on disk: '{srcAbsolute}'.");
            }

            // Copy into Xcode project root.
            var keyFileName = Path.GetFileName(srcAbsolute);
            var destAbsolute = Path.Combine(pathToBuiltProject, keyFileName);
            File.Copy(srcAbsolute, destAbsolute, overwrite: true);

            // Register in main app target's Copy Bundle Resources.
            var pbxPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var pbx = new PBXProject();
            pbx.ReadFromFile(pbxPath);

            var mainTargetGuid = pbx.GetUnityMainTargetGuid();
            var fileGuid = pbx.AddFile(keyFileName, keyFileName, PBXSourceTree.Source);
            pbx.AddFileToBuildSection(mainTargetGuid,
                pbx.GetResourcesBuildPhaseByTarget(mainTargetGuid),
                fileGuid);

            pbx.WriteToFile(pbxPath);
        }

        // -- Embed vendored dynamic xcframeworks (callbackOrder 90) ---------
        //
        // Why a separate callback at 90: EDM4U's pod-install runs at order 50
        // (BUILD_ORDER_INSTALL_PODS), so Pods/ exists only after that. The
        // existing OnPostProcessBuild(50) above does plist + key-file work that
        // doesn't depend on Pods/, so it's fine to share order 50 with EDM4U.
        // This embed pass must read Pods/ → split it out at 90.
        //
        // What it fixes: Unity 2019.3+ writes the Podfile with `use_frameworks!
        // :linkage => :static` (EDM4U default `PodfileStaticLinkFrameworks`).
        // Pods are attached to the `UnityFramework` target only — the .app
        // target `Unity-iPhone` has no pods declared. In that combination
        // CocoaPods does *not* generate any frameworks.sh embed script, so
        // vendored *dynamic* xcframeworks (DaroMObjCBridge + transitive
        // mediation network frameworks like ATOM, AppLovinSDK, InMobiSDK, etc.)
        // never reach `Unity-iPhone.app/Frameworks/` and dyld fails at app
        // launch (`Library not loaded: @rpath/<X>.framework/<X>`).
        //
        // Industry reference: AppLovin MAX Unity Plugin's `AppLovinPostProcessiOS.
        // EmbedDynamicLibrariesIfNeeded` does the same pattern at order 90 —
        // walk Pods/, find vendored dynamic frameworks, register each via
        // `PBXProject.AddFileToEmbedFrameworks(unityMainTargetGuid, ...)`. The
        // resulting Xcode "Embed Frameworks" build phase handles slice
        // selection (ios-arm64 vs simulator) and codesigning natively.
        [PostProcessBuild(90)]
        public static void EmbedDynamicFrameworks(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;

            var podsDir = Path.Combine(pathToBuiltProject, "Pods");
            if (!Directory.Exists(podsDir)) return; // EDM4U disabled or "Generate Podfile" off

            var xcframeworks = Directory.GetDirectories(podsDir, "*.xcframework", SearchOption.AllDirectories);

            // Xcode 16's `builtin-process-xcframework` step (under `[CP] Copy
            // XCFrameworks`) bails with "The signature of X.xcframework cannot
            // be verified" when the vendor's signing cert is revoked. Observed
            // on MolocoSDK 3.12.1 (CSSMERR_TP_CERT_REVOKED). Stripping
            // `_CodeSignature/` makes Xcode treat the framework as unsigned at
            // copy time; the Embed Frameworks phase then re-signs each slice
            // with the consumer's dev identity via CodeSignOnCopy. App Store
            // submission validates the consumer's final signature on the
            // embedded framework — original vendor sig is not what Apple
            // checks at distribution time.
            StripInvalidVendorSignatures(xcframeworks);

            if (!ShouldEmbedDynamicLibraries(pathToBuiltProject)) return;

            var frameworkRelPaths = FindVendoredDynamicFrameworks(xcframeworks, pathToBuiltProject);
            if (frameworkRelPaths.Count == 0) return;

            var pbxPath = PBXProject.GetPBXProjectPath(pathToBuiltProject);
            var pbx = new PBXProject();
            pbx.ReadFromFile(pbxPath);
            var mainTargetGuid = pbx.GetUnityMainTargetGuid();

            foreach (var rel in frameworkRelPaths)
            {
                var fileGuid = pbx.AddFile(rel, rel);
                pbx.AddFileToEmbedFrameworks(mainTargetGuid, fileGuid);
            }

            pbx.WriteToFile(pbxPath);
            DaroLog.Info("Build",
                $"Embedded {frameworkRelPaths.Count} vendored dynamic frameworks into Unity-iPhone");
        }

        private const string UnityIPhoneTargetMarker = "target 'Unity-iPhone' do";
        private const string StaticLinkageMarker = "use_frameworks! :linkage => :static";
        private const string DynamicLinkageMarker = "use_frameworks! :linkage => :dynamic";
        private const string BareUseFrameworksMarker = "use_frameworks!";

        // Truth table (matches AppLovin MAX Unity Plugin convention):
        //
        //   |                       | :linkage=>:dynamic | :linkage=>:static | bare use_frameworks! | none  |
        //   | Unity-iPhone present  | false              | true              | false                | false |
        //   | Unity-iPhone absent   | true               | true              | true                 | true  |
        //
        // When Unity-iPhone target + non-static linkage, CocoaPods auto-generates
        // frameworks.sh and embeds; we'd duplicate. Only :static (or no
        // Unity-iPhone target) needs us to step in.
        internal static bool ShouldEmbedDynamicLibraries(string buildPath)
        {
            var podfilePath = Path.Combine(buildPath, "Podfile");
            if (!File.Exists(podfilePath)) return false;

            var lines = File.ReadAllLines(podfilePath);
            var hasUnityIPhoneTarget = lines.Any(l => l.Contains(UnityIPhoneTargetMarker));
            if (!hasUnityIPhoneTarget) return true;

            var staticIdx = Array.FindIndex(lines, l => l.Contains(StaticLinkageMarker));
            if (staticIdx == -1) return false;

            // CocoaPods uses the last `use_frameworks!` directive when multiple appear.
            var dynamicIdx = Array.FindIndex(lines, l => l.Contains(DynamicLinkageMarker));
            var bareIdx = Array.FindIndex(lines, l => l.Trim() == BareUseFrameworksMarker);
            return staticIdx > dynamicIdx && staticIdx > bareIdx;
        }

        // Returns paths relative to `buildPath`. Only emits xcframeworks whose
        // ios-arm64 slice carries a Mach-O dylib — static archives and
        // missing-slice wrappers are skipped (embedding a static .framework
        // would crash dyld). Xcode's Embed Frameworks phase handles
        // per-config slice selection from the wrapper.
        internal static List<string> FindVendoredDynamicFrameworks(IEnumerable<string> xcframeworks, string buildPath)
        {
            var results = new List<string>();
            // Trailing separator prevents `/build` from spuriously matching `/build-other`.
            var normalizedBuildPath = Path.GetFullPath(buildPath).TrimEnd('/', '\\') + Path.DirectorySeparatorChar;

            foreach (var xcfw in xcframeworks)
            {
                var sliceDir = Path.Combine(xcfw, "ios-arm64");
                if (!Directory.Exists(sliceDir)) continue;

                var probedDynamic = false;
                foreach (var fw in Directory.GetDirectories(sliceDir, "*.framework"))
                {
                    var fwName = Path.GetFileNameWithoutExtension(fw);
                    var binary = Path.Combine(fw, fwName);
                    if (!File.Exists(binary)) continue;
                    if (IsDynamicMachO(binary)) { probedDynamic = true; break; }
                }
                if (!probedDynamic) continue;

                var normalizedXcfw = Path.GetFullPath(xcfw);
                if (!normalizedXcfw.StartsWith(normalizedBuildPath, StringComparison.Ordinal)) continue;

                results.Add(normalizedXcfw.Substring(normalizedBuildPath.Length));
            }
            return results;
        }

        // Conservative: only drops signatures that would already make Xcode
        // fail. Untouched when verify succeeds or no signature is present.
        internal static void StripInvalidVendorSignatures(IEnumerable<string> xcframeworks)
        {
            foreach (var xcfw in xcframeworks)
            {
                var rootSig = Path.Combine(xcfw, "_CodeSignature");
                if (!Directory.Exists(rootSig)) continue;
                if (CodesignVerifyOk(xcfw)) continue;

                Directory.Delete(rootSig, true);
                foreach (var slice in Directory.GetDirectories(xcfw))
                {
                    foreach (var fw in Directory.GetDirectories(slice, "*.framework"))
                    {
                        var fwSig = Path.Combine(fw, "_CodeSignature");
                        if (Directory.Exists(fwSig)) Directory.Delete(fwSig, true);
                    }
                }
                DaroLog.Warn("Build",
                    $"Stripped invalid/revoked signature from {Path.GetFileName(xcfw)}; " +
                    "consumer's dev identity will re-sign on embed.");
            }
        }

        internal static bool CodesignVerifyOk(string path)
        {
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/codesign",
                    Arguments = $"--verify --strict \"{path}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = System.Diagnostics.Process.Start(psi);
                p.WaitForExit();
                return p.ExitCode == 0;
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // codesign binary not reachable (non-macOS host running the
                // hook by mistake). Treat as OK so we don't strip blindly.
                return true;
            }
        }

        // ios-arm64 slice is little-endian, so Mach-O 64 magic surfaces as
        // 0xfeedfacf via BitConverter; filetype field at offset 12 must be 6
        // (MH_DYLIB). Static archives use `!<arch>\n` and fail the magic check.
        internal static bool IsDynamicMachO(string binaryPath)
        {
            try
            {
                using var fs = new FileStream(binaryPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var hdr = new byte[16];
                if (fs.Read(hdr, 0, 16) != 16) return false;

                var magic = BitConverter.ToUInt32(hdr, 0);
                bool is64 = magic == 0xfeedfacf;
                bool is32 = magic == 0xfeedface;
                if (!is64 && !is32) return false;

                var filetype = BitConverter.ToUInt32(hdr, 12);
                const uint MH_DYLIB = 6;
                return filetype == MH_DYLIB;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // -- SKAdNetwork ID source -------------------------------------------

        // Reads SDK/Editor/DaroSkAdNetworkIds.txt — one ID per line, '#' comments allowed.
        // v1 ships as placeholder; populate with Daro/AppLovin SKAdNetwork list later.
        internal static IEnumerable<string> LoadSkAdNetworkIds()
        {
            var guids = AssetDatabase.FindAssets("DaroSkAdNetworkIds t:TextAsset");
            if (guids.Length == 0) return System.Array.Empty<string>();

            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.TextAsset>(
                AssetDatabase.GUIDToAssetPath(guids[0]));
            if (asset == null) return System.Array.Empty<string>();

            return asset.text
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrEmpty(line) && !line.StartsWith("#"));
        }
    }
}
