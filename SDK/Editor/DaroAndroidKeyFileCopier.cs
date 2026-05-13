using System.IO;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;

namespace Daro.Editor
{
    // Copies the consumer's android-daro-key.txt into the gradle project's
    // `launcher/` module root — matching the Daro guide's
    //
    //     app/
    //     └── android-daro-key.txt
    //
    // structure for a standard Android project (Unity's `launcher/` module ≡
    // the guide's `app/` module). The Daro gradle plugin reads it relative to
    // the application module's project directory at build time.
    //
    // Why a separate post-processor (callbackOrder = 51) instead of folding
    // into DaroAndroidPostProcessor (gradle text patches at order 50):
    //   - Keyfile copy is plain file ops — no gradle DSL knowledge required.
    //     Order 51 ensures the gradle tree from order-50 patches is settled
    //     before we touch launcher/.
    //   - Source path comes from settings.androidKeyFile (asset reference),
    //     not from a Unity-routed location. Unity does NOT auto-route .txt
    //     files to launcher/ — consumer placement is free, we resolve via
    //     AssetDatabase.GetAssetPath.
    //
    // **Path semantics**: Unity passes the *unityLibrary* subdirectory to
    // `OnPostGenerateGradleAndroidProject`, NOT the export root. Same quirk
    // documented in DaroAndroidPostProcessor.
    //
    // settings.androidKeyFile null is a defensive no-op — the validator's
    // ANDROID_KEY_FILE_MISSING Fail at IPreprocessBuildWithReport (order 0)
    // already blocks the build before we get here in any normal flow.
    public sealed class DaroAndroidKeyFileCopier : IPostGenerateGradleAndroidProject
    {
        private const string KeyFileName = "android-daro-key.txt";

        public int callbackOrder => 51;

        public void OnPostGenerateGradleAndroidProject(string path)
        {
            Run(DaroSettingsLocator.FindOrNull(), path);
        }

        // testable seam — see plan D8-G.
        internal static void Run(DaroSettings settings, string unityLibraryPath)
        {
            if (settings == null || settings.androidKeyFile == null)
                return; // validator already blocked the build for this in production

            var rootPath = Path.GetDirectoryName(unityLibraryPath);
            if (string.IsNullOrEmpty(rootPath)) return;

            var assetPath = AssetDatabase.GetAssetPath(settings.androidKeyFile);
            var sourceAbsolute = Path.GetFullPath(assetPath);

            if (!File.Exists(sourceAbsolute))
            {
                throw new BuildFailedException(
                    $"[Daro] android-daro-key.txt source not found on disk at '{sourceAbsolute}'. " +
                    $"DaroSettings.androidKeyFile references a broken asset (deleted, git LFS miss, etc.).");
            }

            // Per Daro guide: file goes at the launcher module *root*
            // (alongside build.gradle), NOT under src/main/.
            var destDir = Path.Combine(rootPath, "launcher");
            Directory.CreateDirectory(destDir);

            var destAbsolute = Path.Combine(destDir, KeyFileName);
            File.Copy(sourceAbsolute, destAbsolute, overwrite: true);
        }
    }
}
