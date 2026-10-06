using System;
using System.Collections.Generic;

namespace Daro.Editor
{
    // Pure compute layer — produces the *content* (gradle coords / property
    // keys / proguard rules) that the Android post-processor needs to
    // inject. File IO and Unity-API calls live in the apply layer
    // (DaroAndroidPostProcessor). Splitting this layer means content vs.
    // apply drift is structurally impossible.
    //
    // No Unity APIs here. Tests run in EditMode without any Editor state.
    //
    // Source of truth for content: guide.daro.so/ko/sdk-integration/android.
    //
    // Scope guardrail — what *belongs* in this content vs. EDM4U:
    //   * EDM4U handles maven artifact resolution + project repos. Those
    //     live in DaroDependencies.xml (`<androidPackage>` + nested
    //     `<repositories>`). NOT here.
    //   * This file is for build-config that EDM4U does not own: gradle
    //     plugin classpaths, gradle.properties keys, proguard rules,
    //     minSdk floor.
    internal static class DaroAndroidGradleContent
    {
        // Root buildscript classpath entries. Daro plugin + AppLovin Quality
        // Service Gradle plugin (per Daro guide — required for MAX mediation).
        // Gradle plugin classpaths are not maven artifacts in the EDM4U
        // sense (they go into buildscript.dependencies, not the project's
        // `dependencies { implementation }` block), so EDM4U does not cover
        // them. Hence post-process injection.
        //
        // Kotlin Gradle Plugin is no longer injected — the shim is now shipped
        // as a prebuilt AAR (SDK/Plugins/Android/daro-android-wrapper.aar),
        // so the consumer's build never compiles our Kotlin source.
        internal const string DaroPluginCoords = "so.daro:daro-plugin:2.0.3";
        internal const string AppLovinQualityServiceClasspath =
            "com.applovin.quality:AppLovinQualityServiceGradlePlugin:5.5.2";

        // daro-sdk requires Android API 23+.
        internal const int MinSdk = 23;

        // Daro keep rules. The shim's own AAR embeds so.daro.* consumer rules,
        // but the exported Gradle project may omit UPM-package-level
        // proguard-user.txt files. Mirror the daro-sdk JNI/reflection keeps here
        // so PatchProguard delivers them through the stable export hook.
        internal const string ProguardKeepRule =
            "-keep class so.daro.** { *; }\n" +
            "\n" +
            "-keep class droom.daro.core.model.DaroAdLoadError { *; }\n" +
            "-keep class droom.daro.core.model.DaroAdDisplayFailError { *; }\n" +
            "-keep class droom.daro.core.model.DaroAdInfo { *; }\n" +
            "-keep class droom.daro.core.model.DaroRewardedAd$DaroRewardedItem { *; }\n" +
            "\n" +
            "-keepclassmembers class droom.daro.view.DaroAdView {\n" +
            "    public void setAutoDetectLifecycle(boolean);\n" +
            "    public void setRefreshSeconds(int);\n" +
            "}";

        // AppLovin maven URL — needed in the *root buildscript.repositories*
        // so the AppLovinQualityServiceGradlePlugin classpath can resolve
        // at config time. Project-level (settings.gradle) repos are EDM4U's
        // territory and live in DaroDependencies.xml; this single URL is
        // the buildscript-side counterpart that EDM4U does not handle.
        internal const string AppLovinMavenUrl = "https://artifacts.applovin.com/android";

        // Classpaths for root build.gradle's buildscript.dependencies block.
        internal static readonly IReadOnlyList<string> BuildscriptClasspaths = new[]
        {
            DaroPluginCoords,
            AppLovinQualityServiceClasspath,
        };

        private static readonly IReadOnlyDictionary<string, string> EmptyProps =
            new Dictionary<string, string>(0);

        // True iff the post-processor should apply patches. False = silent
        // no-op. Validator (BuildValidator order=0) already fails fast on an
        // empty INTEGRATION KEY — this is a defense-in-depth so callers can
        // compose ShouldApply without a separate null check.
        internal static bool ShouldApply(DaroSettings settings) =>
            settings != null && !string.IsNullOrEmpty(settings.androidIntegrationKey);

        // Gradle plugin id applied to the launcher's plugins block. The
        // unified generation registers `so.daro` as the canonical id
        // (`so.daro.a` / `so.daro.m` remain as aliases of the same plugin
        // class) — mediation is a runtime routing concern, not a build-time
        // variant, so the id no longer branches.
        internal const string GradlePluginId = "so.daro";

        internal const string SafeDkWorkaroundProperty = "daro.safedkConfigurationCacheWorkaround";

        // Integration key remains additive; an explicitly selected workaround overrides
        // the customer's value. PreserveExisting never supplies a replacement.
        internal static IReadOnlyDictionary<string, string> GetGradleProperties(DaroSettings settings)
        {
            if (!ShouldApply(settings)) return EmptyProps;
            var props = new Dictionary<string, string>
            {
                ["daroIntegrationKey"] = settings.androidIntegrationKey,
            };
            switch (settings.androidSafedkConfigurationCacheWorkaround)
            {
                case SafeDkConfigurationCacheWorkaround.Enabled:
                    props[SafeDkWorkaroundProperty] = "true";
                    break;
                case SafeDkConfigurationCacheWorkaround.Disabled:
                    props[SafeDkWorkaroundProperty] = "false";
                    break;
            }
            return props;
        }
    }
}
