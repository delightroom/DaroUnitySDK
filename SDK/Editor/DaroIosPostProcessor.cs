using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
    // settled.
    //
    // Info.plist additive injection — NSUserTrackingUsageDescription /
    // SKAdNetworkItems (merged with any existing list). All injections are
    // *additive only* — existing non-empty consumer values survive untouched.
    // The SDK never injects an ATS (NSAppTransportSecurity) exception: MAX
    // requires none, and weakening app-wide transport security is the
    // consumer's call (see docs/study/ios-ats-mediation.md).
    //
    // INTEGRATION KEY 는 여기서 다루지 않는다 — order 45 의
    // PrepareIntegrationKey 가 봉투를 심고 Podfile 에 훅을 건다.
    //
    // 키파일을 Copy Bundle Resources 에 넣던 PBX 단계는 봉투 전환으로
    // 사라졌다(DARO-1434) — 배달할 파일 자체가 없다.
    //
    // The plist branch is automatically tested via ApplyPlistChanges seam.
    public static class DaroIosPostProcessor
    {
        // -- INTEGRATION KEY 준비 (callbackOrder 45) ------------------------
        //
        // EDM4U 의 `pod install`(order 50) 이 소비할 두 가지를 그 전에 놓는다.
        //   1. 소스 Info.plist 의 `DaroIntegrationKey` — 봉투 암호문
        //   2. Podfile 의 `post_install` — pod 에 실려 온 정본 훅을 부른다
        //
        // 그러면 평문 네 값(GADApplicationIdentifier / DaroAppKey /
        // DaroAppLovinSdkKey / DaroAppLovinAdReviewKey)은 `pod install` 중에
        // 정본 훅이 심는다. 암호문은 지우지 않는다 — 런타임 해제 경로가 쓴다.
        //
        // **왜 우리가 CLI 를 직접 부르지 않나** — 정본은 pod 에 함께 실려 오는
        // `Scripts/daro_integration_key.rb` 이고 RN·Flutter 가 그것을 쓴다.
        // Podfile 에 걸어두면 주입이 Unity 빌드가 아니라 `pod install` 을
        // 따라다닌다 — EDM4U 의 pod install 을 끄고 CI 가 나중에 따로 돌리는
        // 구성에서도 그대로 동작한다. 우리가 프로세스를 띄우면 그 구성에서
        // 주입이 통째로 빠진다.
        //
        // **왜 45 인가** — 실측(빈 훅을 여러 order 에 꽂아 확인)하면 Podfile 은
        // order 41 시점에 이미 있고 `Pods/` 는 50 이후에 생긴다. 41~49 가 통째로
        // 비어 있어 그 사이면 어디든 된다.
        //
        // **왜 봉투를 50 이 아니라 여기서 심나** — 정본 훅은 `--key` 없이 CLI 를
        // 불러 plist 의 `DaroIntegrationKey` 를 읽는다. 우리 order 50 과 EDM4U 의
        // pod install 이 둘 다 50 이라 상대 순서가 정의돼 있지 않다. 45 에 심으면
        // 어느 쪽이 먼저 돌든 훅이 읽을 값이 있다.
        //
        // **왜 Xcode 빌드 페이즈가 아닌가** — 그 설계는 폐기됐다(DARO-1120).
        // 산출물 plist 를 매 빌드 고치면 코드 서명이 깨진다 — Info.plist 의
        // SHA-256 이 CodeDirectory 특별 슬롯 -1 에 박히는데 페이즈는 매 빌드
        // 돌고 CodeSign 은 건너뛰어, 기기 설치가 거부된다(DARO-1112 실측).
        [PostProcessBuild(45)]
        public static void PrepareIntegrationKey(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;

            var settings = DaroSettingsLocator.FindOrNull();
            if (settings == null) return;                                      // validator already blocked
            if (string.IsNullOrWhiteSpace(settings.iosIntegrationKey)) return; // 같은 이유

            WriteIntegrationKeyEnvelope(settings.iosIntegrationKey,
                                        Path.Combine(pathToBuiltProject, "Info.plist"));

            var podfilePath = Path.Combine(pathToBuiltProject, "Podfile");
            if (!File.Exists(podfilePath))
            {
                // 던지지 않는다 — Podfile 이 없다는 것은 EDM4U 가 꺼져 있다는
                // 뜻이고, 그러면 광고 SDK 자체가 안 붙어 이 키 하나가 문제인
                // 상황이 아니다. 다만 조용히 넘기지도 않는다.
                DaroLog.Warn("Build",
                    "Podfile not found — the INTEGRATION KEY will not be unlocked. " +
                    "Injection ships as a CocoaPods post_install hook inside the Daro pod, " +
                    "so it needs EDM4U's iOS Resolver (Podfile generation) enabled.");
                return;
            }

            var patched = AddIntegrationKeyPostInstall(File.ReadAllText(podfilePath));
            if (patched != null) File.WriteAllText(podfilePath, patched);
        }

        // 봉투 암호문만 심는다. 평문 네 값은 `pod install` 중에 정본 훅이 채운다 —
        // 해제 시크릿은 네이티브 도구에만 있고 Editor 는 복호화하지 않는다.
        internal static void WriteIntegrationKeyEnvelope(string envelope, string plistFullPath)
        {
            var plist = new PlistDocument();
            plist.ReadFromFile(plistFullPath);
            plist.root.SetString("DaroIntegrationKey", envelope);
            plist.WriteToFile(plistFullPath);
        }

        internal const string PodfileHookMarker = "# >>> Daro INTEGRATION KEY >>>";

        // Podfile 에 정본 훅 호출을 심는다. 이미 있으면 null 을 돌려준다.
        //
        // `post_install` 은 Podfile 에 **하나만** 둘 수 있다 — 두 번 선언하면
        // CocoaPods 가 `Specifying multiple post_install hooks is unsupported` 로
        // 죽는다. 그래서 있으면 그 블록 안에 끼워 넣고, 없을 때만 새로 만든다.
        internal static string AddIntegrationKeyPostInstall(string podfile)
        {
            if (podfile.Contains(PodfileHookMarker)) return null;   // 이미 걸려 있다

            var existing = Regex.Match(
                podfile,
                @"^([ \t]*)post_install\s+do\s*\|\s*([A-Za-z_]\w*)\s*\|[ \t]*$",
                RegexOptions.Multiline);

            if (!existing.Success)
            {
                // `post_install` 자체가 없으면 우리 블록을 새로 붙인다.
                var block = "\npost_install do |installer|\n" + HookBody("installer", "  ") + "end\n";
                return podfile.TrimEnd('\n') + "\n" + block;
            }

            // 소비자(또는 다른 플러그인)의 블록이 이미 있다. 그 안에 끼운다 —
            // 블록 변수 이름이 `installer` 가 아닐 수 있으므로 잡아서 쓴다.
            var indent = existing.Groups[1].Value + "  ";
            var installerVar = existing.Groups[2].Value;
            var insertAt = existing.Index + existing.Length;
            return podfile.Substring(0, insertAt) + "\n" +
                   HookBody(installerVar, indent).TrimEnd('\n') +
                   podfile.Substring(insertAt);
        }

        private static string HookBody(string installerVar, string indent)
        {
            // 경로를 박지 않는다 — 이 훅이 실리는 pod 이름과 깊이가 채널마다
            // 다르다(공개 zip 은 `Scripts/`, 내부 스펙 레포는 `<pod>/<버전>/Scripts/`).
            // `__dir__`(Podfile 위치)에 앵커한다 — 상대경로는 `pod install` 의
            // CWD 를 타는데 하위 디렉토리나 `--project-directory` 로도 호출된다.
            return
                indent + PodfileHookMarker + "\n" +
                indent + "hook = Dir.glob(File.expand_path('Pods/*/**/Scripts/daro_integration_key.rb', __dir__)).first\n" +
                indent + "raise '[Daro] INTEGRATION KEY hook missing from the Daro pod' unless hook\n" +
                indent + "require hook\n" +
                indent + "Daro::IntegrationKey.install!(" + installerVar + ")\n" +
                indent + "# <<< Daro INTEGRATION KEY <<<\n";
        }

        // -- INTEGRATION KEY 주입 확인 (callbackOrder 51) --------------------
        //
        // 주입 자체는 `pod install` 안에서 정본 훅이 한다(order 45 가 걸어둔다).
        // 문제는 **`pod install` 이 실패해도 Unity 빌드는 성공으로 끝난다**는 것이다
        // — EDM4U 가 실패를 로그로만 남기고 BuildFailedException 을 던지지 않는다
        // (실측: 훅이 raise 한 빌드가 `result=Succeeded` 로 끝났다).
        //
        // 그대로 두면 평문 자격증명 없는 바이너리가 나가고 앱은 기동 직후
        // 크래시한다 — 빌드 로그를 안 읽으면 안 보인다. 그 창을 여기서 닫는다.
        //
        // 값이 비어 있는 것도 없는 것으로 친다. 봉투가 빈 `admobAppKey` 를 실어
        // 오면 CLI 가 빈 `GADApplicationIdentifier` 를 쓰는데, 그것 역시 앱을
        // 기동 직후 죽인다.
        [PostProcessBuild(51)]
        public static void VerifyIntegrationKeyInjected(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;

            var settings = DaroSettingsLocator.FindOrNull();
            if (settings == null) return;                                      // validator already blocked
            if (string.IsNullOrWhiteSpace(settings.iosIntegrationKey)) return; // 같은 이유

            // 이 빌드에서 `pod install` 이 안 돌았으면 아직 없는 것이 정상이다 —
            // 나중에 누가 `pod install` 을 돌릴 때 Podfile 의 훅이 심는다.
            // 여기서 판정하면 그 구성을 잘못 막는다.
            if (!Directory.Exists(Path.Combine(pathToBuiltProject, "Pods")))
            {
                DaroLog.Warn("Build",
                    "pod install did not run in this build — INTEGRATION KEY values are not in " +
                    "Info.plist yet. The Podfile carries the injection hook, so they land when " +
                    "pod install runs. Verify Info.plist after that step.");
                return;
            }

            var missing = MissingInjectedKeys(Path.Combine(pathToBuiltProject, "Info.plist"));
            if (missing.Count > 0)
            {
                throw new BuildFailedException(
                    "[Daro] INTEGRATION KEY was not injected — Info.plist is missing " +
                    string.Join(", ", missing) + ". pod install runs the injection hook, so a " +
                    "failed pod install leaves the app without its ad credentials and it will " +
                    "crash at launch. Read the pod install output above for the reason.");
            }
        }

        // 정본 훅이 심어야 하는 네 값 중 없거나 빈 것.
        internal static List<string> MissingInjectedKeys(string plistFullPath)
        {
            var plist = new PlistDocument();
            plist.ReadFromFile(plistFullPath);
            var root = plist.root;

            var missing = new List<string>();
            foreach (var key in InjectedPlistKeys)
            {
                if (!root.values.TryGetValue(key, out var element)) { missing.Add(key); continue; }

                string value;
                try { value = element.AsString(); }
                catch { value = null; }          // 문자열이 아니면 우리가 아는 모양이 아니다
                if (string.IsNullOrEmpty(value)) missing.Add(key);
            }
            return missing;
        }

        internal static readonly string[] InjectedPlistKeys =
        {
            "GADApplicationIdentifier",
            "DaroAppKey",
            "DaroAppLovinSdkKey",
            "DaroAppLovinAdReviewKey",
        };

        [PostProcessBuild(50)]
        public static void OnPostProcessBuild(BuildTarget target, string pathToBuiltProject)
        {
            if (target != BuildTarget.iOS) return;

            var settings = DaroSettingsLocator.FindOrNull();
            if (settings == null) return; // validator already blocked

            var plistPath = Path.Combine(pathToBuiltProject, "Info.plist");
            ApplyPlistChanges(settings, plistPath, LoadSkAdNetworkIds());
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

            // SWIFT_VERSION 은 남긴다. 원래는 Plugins/iOS 의 Swift 헬퍼
            // (DaroUnityRevenueToken.swift — CryptoKit 토큰 유도)를 컴파일하려고
            // 걸었는데 DARO-1434 가 그 파일을 지워 **shim 에 Swift 소스가 0개**다.
            //
            // 그래도 지우지 않는 이유: 통합 pod(DaroObjCBridge)이 Swift 를 품은
            // static framework 라, 호스트 타깃의 Swift 설정이 링크에 관여할 수
            // 있다. 안 쓰이면 무해하고 필요했는데 지우면 앱 기동 시점에 터진다 —
            // 비용이 비대칭이라 남기는 쪽을 골랐다.
            //
            // 판정은 샘플 iOS 실빌드가 한다(DARO-1434 완료 조건). 거기서 불필요가
            // 확인되면 이 줄과 이 주석을 함께 지운다.
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

            // DaroIntegrationKey(봉투)는 여기가 아니라 order 45 가 심는다 —
            // pod install 이 그것을 읽어야 하는데 우리 50 과 EDM4U 의
            // pod install 이 둘 다 50 이라 상대 순서가 없다.

            // ATT description — inject when set. validator gates empty.
            if (!string.IsNullOrEmpty(settings.attPromptDescription))
                root.SetString("NSUserTrackingUsageDescription", settings.attPromptDescription);

            // SKAdNetworkItems — merge-additive (dedupe by SKAdNetworkIdentifier).
            MergeSkAdNetworkItems(root, skAdNetworkIds);

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

        // -- Embed vendored dynamic xcframeworks (callbackOrder 90) ---------
        //
        // Why a separate callback at 90: EDM4U's pod-install runs at order 50
        // (BUILD_ORDER_INSTALL_PODS), so Pods/ exists only after that. The
        // existing OnPostProcessBuild(50) above does plist work that doesn't
        // depend on Pods/, so it's fine to share order 50 with EDM4U.
        // This embed pass must read Pods/ → split it out at 90.
        //
        // What it fixes: Unity 2019.3+ writes the Podfile with `use_frameworks!
        // :linkage => :static` (EDM4U default `PodfileStaticLinkFrameworks`).
        // Pods are attached to the `UnityFramework` target only — the .app
        // target `Unity-iPhone` has no pods declared. In that combination
        // CocoaPods does *not* generate any frameworks.sh embed script, so
        // vendored *dynamic* xcframeworks (전이로 딸려오는 미디에이션 네트워크
        // 프레임워크 — AppLovinSDK, InMobiSDK, MolocoSDK 등)
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
