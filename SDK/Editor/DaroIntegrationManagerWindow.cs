using System;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace Daro.Editor
{
    // Daro Integration Manager — single Editor window that fronts DaroSettings
    // editing, build-validation visualization, and EDM4U Force Resolve.
    // Backbone classes (Locator / Validator / EdmChecker) own the logic; this
    // window is a thin shell that binds DaroSettings via SerializedObject and
    // renders ValidationResult rows produced by DaroValidationRowFactory.
    //
    // Visible copy is sourced from DaroImLocalization (EN/KO). UXML carries no
    // localizable text — everything is named and populated in C# at CreateGUI
    // and on language change. BuildValidator logs / BuildFailedException stay
    // English so build reports remain grep-friendly.
    //
    // Window-class rendering / SerializedObject binding / VisualElement tree
    // wiring sit poorly under EditMode tests — verification is sprint-exit
    // smoke. The only EditMode-tested seam in this file is the row-factory
    // (separate class).
    public sealed class DaroIntegrationManagerWindow : EditorWindow
    {
        // Fixed-path asset creation — sketch decision (avoids SaveFilePanel
        // for first-time setup; consumer can move/rename later).
        private const string CreatePath = "Assets/Daro/DaroSettings.asset";
        private const string CreateDir  = "Assets/Daro";

        private DaroSettings     _settings;
        private SerializedObject _serializedSettings;

        // Named element cache (populated in CreateGUI)
        private VisualElement _noSettingsPanel;
        private VisualElement _settingsPanel;
        private Label         _assetPathLabel;
        private VisualElement _validationList;
        private VisualElement _iosGroup;
        private Button        _resolveIosBtn;
        private Label         _osWarningLabel;
        private bool          _osWarningActive;
        private Label         _validateIosResult;
        private Label         _validateAndroidResult;
        private Toggle        _aiHelperToggle;
        private Label         _aiHelperHelp;
        private Label         _aiHelperNotice;

        private Action _localizationHandler;

        [MenuItem("Daro/Integration Manager")]
        public static void Open() =>
            GetWindow<DaroIntegrationManagerWindow>("Daro Integration Manager").Show();

        private void CreateGUI()
        {
            var tree  = LoadAsset<VisualTreeAsset>("DaroIntegrationManagerWindow", "VisualTreeAsset");
            var style = LoadAsset<StyleSheet>("DaroIntegrationManagerWindow", "StyleSheet");

            if (tree == null || style == null)
            {
                rootVisualElement.Add(new Label(DaroImLocalization.Get("error.uiAssetsMissing")));
                return;
            }

            tree.CloneTree(rootVisualElement);
            rootVisualElement.styleSheets.Add(style);

            var logo = LoadAsset<Texture2D>("DARO_logo_white", "Texture2D");
            var logoEl = rootVisualElement.Q<Image>("im-logo");
            if (logo != null && logoEl != null) logoEl.image = logo;

            _noSettingsPanel       = rootVisualElement.Q("im-no-settings-panel");
            _settingsPanel         = rootVisualElement.Q("im-settings-panel");
            _assetPathLabel        = rootVisualElement.Q<Label>("im-asset-path-label");
            _validationList        = rootVisualElement.Q("im-validation-list");
            _iosGroup              = rootVisualElement.Q("im-ios-group");
            _resolveIosBtn         = rootVisualElement.Q<Button>("im-resolve-ios-btn");
            _osWarningLabel        = rootVisualElement.Q<Label>("im-os-warning-label");
            _validateIosResult     = rootVisualElement.Q<Label>("im-validate-ios-result");
            _validateAndroidResult = rootVisualElement.Q<Label>("im-validate-android-result");
            _aiHelperToggle        = rootVisualElement.Q<Toggle>("im-ai-helper-toggle");
            _aiHelperHelp          = rootVisualElement.Q<Label>("im-ai-helper-help");
            _aiHelperNotice        = rootVisualElement.Q<Label>("im-ai-helper-notice");

            WireLanguageSelector();
            WireButtons();
            WireAiHelper();
            ApplyOsRestrictions();

            _localizationHandler = OnLanguageChanged;
            DaroImLocalization.Changed += _localizationHandler;

            ApplyLocalization();
            BindOrShowNoSettings();
        }

        private void OnDisable()
        {
            if (_localizationHandler != null)
            {
                DaroImLocalization.Changed -= _localizationHandler;
                _localizationHandler = null;
            }
        }

        private void OnEnable()
        {
            // CreateGUI may not have run yet on first enable / domain reload;
            // BindOrShowNoSettings touches cached elements that don't exist
            // until then.
            if (_validationList == null) return;
            BindOrShowNoSettings();
        }

        private void OnFocus()
        {
            if (_validationList == null) return;
            RefreshValidation();
        }

        private void OnProjectChange()
        {
            if (_validationList == null) return;
            BindOrShowNoSettings();
        }

        private void OnLanguageChanged()
        {
            if (rootVisualElement == null) return;
            ApplyLocalization();
            RefreshValidation();
        }

        private void WireLanguageSelector()
        {
            var langField = rootVisualElement.Q<EnumField>("im-lang-field");
            if (langField == null) return;
            langField.Init(DaroImLocalization.Current);
            langField.RegisterValueChangedCallback(evt =>
            {
                DaroImLocalization.Current = (DaroImLocalization.Lang)evt.newValue;
            });
        }

        private void WireButtons()
        {
            rootVisualElement.Q<Button>("im-create-settings-btn")?.RegisterCallback<ClickEvent>(_ =>
                CreateSettingsAsset());

            rootVisualElement.Q<Button>("im-resolve-android-btn")?.RegisterCallback<ClickEvent>(_ =>
                DaroEdmChecker.TryForceResolveAndroid());

            _resolveIosBtn?.RegisterCallback<ClickEvent>(_ =>
                DaroEdmChecker.TryForceResolveIos());

            rootVisualElement.Q<Button>("im-validate-btn")?.RegisterCallback<ClickEvent>(_ =>
                RefreshValidation());

            rootVisualElement.Q<Button>("im-validate-ios-btn")?.RegisterCallback<ClickEvent>(_ =>
                RunKeyfileValidation(
                    iosNotAndroid: true,
                    appKey: _settings?.iosDaroAppKey,
                    keyfile: _settings?.iosKeyFile));

            rootVisualElement.Q<Button>("im-validate-android-btn")?.RegisterCallback<ClickEvent>(_ =>
                RunKeyfileValidation(
                    iosNotAndroid: false,
                    appKey: _settings?.androidDaroAppKey,
                    keyfile: _settings?.androidKeyFile));

            // Mediation EnumField is hidden in v1 (single-value enum) but the
            // change-callback wiring is in place so v2 AdMob introduction
            // doesn't need a window-class change.
            var mediationField = rootVisualElement.Q<EnumField>("im-mediation-field");
            mediationField?.RegisterValueChangedCallback(_ =>
            {
                _serializedSettings?.ApplyModifiedProperties();
                RefreshValidation();
            });
        }

        // AI Integration Helper toggle — on ChangeEvent, runs the
        // canonical reconcile sequence (see DaroAiKbReconciler) against the
        // new toggle value, then refreshes UI state. The reconcile body
        // itself — legacy CLAUDE.md sweep, KB copy, marker inject,
        // env-gated own-file Apply/Clean — lives in DaroAiKbReconciler so
        // Bootstrap and this toggle handler stay in sync.
        private void WireAiHelper()
        {
            if (_aiHelperToggle == null) return;
            _aiHelperToggle.RegisterValueChangedCallback(evt =>
            {
                DaroAiKbReconciler.ReconcileSync(evt.newValue);
                _serializedSettings?.ApplyModifiedProperties();
                UpdateAiHelperNotice();
                RefreshValidation();
            });
        }

        // Notice visible when the toggle is ON and at least one observable
        // issue exists: no AI agent environment signaled (so reconciliation
        // is a no-op), or a Cline single-file conflict (own-file axis
        // blocked for Cline only). Called from CreateGUI / Bind / toggle
        // ChangeEvent / OnLanguageChanged so the label always reflects
        // current state.
        private void UpdateAiHelperNotice()
        {
            if (_aiHelperNotice == null) return;
            var toggleOn = _settings != null && _settings.enableAiIntegrationHelper;
            if (!toggleOn)
            {
                _aiHelperNotice.AddToClassList("im-hidden");
                return;
            }

            string text = null;
            var root = DaroProjectRoot.Path;

            // No env signal anywhere — nothing to reconcile.
            if (!DaroAiKbTargets.AnyEnvSignal())
                text = DaroImLocalization.Get("ai.noAgentEnv");

            // Cline `.clinerules` single-file conflict — own-file axis blocked for Cline.
            foreach (var target in DaroAiKbTargets.OwnFileTargets)
            {
                var conflict = target.ConflictGuard?.Invoke(root);
                if (string.IsNullOrEmpty(conflict)) continue;
                var conflictMsg = DaroImLocalization.Get("ai.clineFileMode");
                text = string.IsNullOrEmpty(text) ? conflictMsg : text + "\n" + conflictMsg;
                break;
            }

            if (!string.IsNullOrEmpty(text))
            {
                _aiHelperNotice.text = text;
                _aiHelperNotice.RemoveFromClassList("im-hidden");
            }
            else
            {
                _aiHelperNotice.AddToClassList("im-hidden");
            }
        }

        private void ApplyOsRestrictions()
        {
#if UNITY_EDITOR_WIN
            if (_resolveIosBtn != null)
            {
                _resolveIosBtn.SetEnabled(false);
                _resolveIosBtn.tooltip = DaroImLocalization.Get("os.iosResolveTooltip");
            }
            _iosGroup?.AddToClassList("im-platform-group--os-disabled");
            _osWarningActive = true;
            if (_osWarningLabel != null)
            {
                _osWarningLabel.RemoveFromClassList("im-hidden");
            }
#endif
        }

        // Pumps localized text into every named element. Called on CreateGUI
        // and on DaroImLocalization.Changed. Kept idempotent: every element
        // is null-checked because the same window may be partially built (UI
        // assets missing) when this runs.
        private void ApplyLocalization()
        {
            SetLabel("im-title-label",                  "window.title");
            SetLabel("im-nosettings-warning-label",     "nosettings.warning");
            SetButton("im-create-settings-btn",         "nosettings.create");

            SetFoldout("im-foldout-settings",           "foldout.settings");
            SetFoldout("im-foldout-nativedeps",         "foldout.nativeDeps");
            SetFoldout("im-foldout-validation",         "foldout.validation");
            SetFoldout("im-foldout-aihelper",           "foldout.aiHelper");

            if (_aiHelperToggle != null)
                _aiHelperToggle.label = DaroImLocalization.Get("ai.toggleLabel");
            if (_aiHelperHelp != null)
                _aiHelperHelp.text = DaroImLocalization.Get("ai.toggleHelp");
            UpdateAiHelperNotice();

            SetLabel("im-section-mediation-title",      "section.mediation");
            SetLabel("im-section-ios-title",            "section.ios");
            SetLabel("im-section-android-title",        "section.android");

            SetEnumLabel("im-mediation-field",          "field.mediation");
            SetTextLabel("im-ios-appkey-field",         "field.daroAppKey");
            SetObjectLabel("im-ios-keyfile-field",      "field.keyFile");
            SetTextLabel("im-ios-admob-field",          "field.adMobKey");
            SetTextLabel("im-ios-att-field",            "field.attDescription");
            SetTextLabel("im-android-appkey-field",     "field.daroAppKey");
            SetObjectLabel("im-android-keyfile-field",  "field.keyFile");

            SetButton("im-resolve-android-btn",         "btn.resolveAndroid");
            SetButton("im-resolve-ios-btn",             "btn.resolveIos");
            SetButton("im-validate-btn",                "btn.runChecks");
            SetButton("im-validate-ios-btn",            "btn.validate");
            SetButton("im-validate-android-btn",        "btn.validate");

            // Reset result labels to idle on language switch — old result
            // text would be in the previous language and confuse the reader.
            ResetValidateResultLabel(_validateIosResult);
            ResetValidateResultLabel(_validateAndroidResult);

            if (_resolveIosBtn != null && !_resolveIosBtn.enabledSelf)
                _resolveIosBtn.tooltip = DaroImLocalization.Get("os.iosResolveTooltip");

            if (_osWarningLabel != null && _osWarningActive)
                _osWarningLabel.text = DaroImLocalization.Get("os.windowsWarning");

            if (_assetPathLabel != null && _settings == null)
                _assetPathLabel.text = string.Empty;
            else if (_assetPathLabel != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(_settings)))
                _assetPathLabel.text = DaroImLocalization.Get("assetPath.unsaved");
        }

        private void SetLabel(string name, string key)
        {
            var el = rootVisualElement.Q<Label>(name);
            if (el != null) el.text = DaroImLocalization.Get(key);
        }

        private void SetButton(string name, string key)
        {
            var el = rootVisualElement.Q<Button>(name);
            if (el != null) el.text = DaroImLocalization.Get(key);
        }

        private void SetFoldout(string name, string key)
        {
            var el = rootVisualElement.Q<Foldout>(name);
            if (el != null) el.text = DaroImLocalization.Get(key);
        }

        private void SetTextLabel(string name, string key)
        {
            var el = rootVisualElement.Q<TextField>(name);
            if (el != null) el.label = DaroImLocalization.Get(key);
        }

        private void SetObjectLabel(string name, string key)
        {
            var el = rootVisualElement.Q<ObjectField>(name);
            if (el != null) el.label = DaroImLocalization.Get(key);
        }

        private void SetEnumLabel(string name, string key)
        {
            var el = rootVisualElement.Q<EnumField>(name);
            if (el != null) el.label = DaroImLocalization.Get(key);
        }

        // Runs DaroKeyfileValidator on the platform's (appKey, keyfile) pair
        // and renders the result inline next to the validate button. This is
        // a *pre-build* equivalent of what the Daro gradle plugin does at
        // configure time — surfaces "Tag mismatch" type errors in O(ms) so
        // the user doesn't have to wait for a full APK build to discover
        // a credential mismatch.
        //
        // The crypto layer (DaroAesGcm) is built atop Aes-ECB primitives
        // available on every Mono platform — no .NET Standard 2.1 floor.
        private void RunKeyfileValidation(bool iosNotAndroid, string appKey, TextAsset keyfile)
        {
            var resultLabel = iosNotAndroid ? _validateIosResult : _validateAndroidResult;
            if (resultLabel == null) return;

            var keyfileText = keyfile != null ? keyfile.text : null;
            var result = DaroKeyfileValidator.Validate(appKey, keyfileText);
            ApplyValidateResult(resultLabel, result);
        }

        private static void ApplyValidateResult(Label label, DaroKeyfileValidator.Result result)
        {
            string key;
            string css;
            switch (result)
            {
                case DaroKeyfileValidator.Result.Valid:
                    key = "validate.valid";          css = "im-validate-result--valid";   break;
                case DaroKeyfileValidator.Result.TagMismatch:
                    key = "validate.tagMismatch";    css = "im-validate-result--invalid"; break;
                case DaroKeyfileValidator.Result.EmptyAppKey:
                    key = "validate.emptyAppKey";    css = "im-validate-result--invalid"; break;
                case DaroKeyfileValidator.Result.NoKeyfile:
                    key = "validate.noKeyfile";      css = "im-validate-result--invalid"; break;
                case DaroKeyfileValidator.Result.InvalidBase64:
                    key = "validate.invalidBase64";  css = "im-validate-result--invalid"; break;
                case DaroKeyfileValidator.Result.TooShort:
                    key = "validate.tooShort";       css = "im-validate-result--invalid"; break;
                default:
                    key = "validate.idle";           css = "im-validate-result--neutral"; break;
            }
            label.text = DaroImLocalization.Get(key);
            ApplyResultClass(label, css);
        }

        private static void ApplyResultClass(Label label, string activeClass)
        {
            label.RemoveFromClassList("im-validate-result--valid");
            label.RemoveFromClassList("im-validate-result--invalid");
            label.RemoveFromClassList("im-validate-result--neutral");
            label.AddToClassList(activeClass);
        }

        private static void ResetValidateResultLabel(Label label)
        {
            if (label == null) return;
            label.text = string.Empty;
            ApplyResultClass(label, "im-validate-result--neutral");
        }

        private void BindOrShowNoSettings()
        {
            _settings = DaroSettingsLocator.FindOrNull(out _);

            // Settings instance changed — any prior validation result label
            // is stale (different appKey/keyfile context). Reset to idle.
            ResetValidateResultLabel(_validateIosResult);
            ResetValidateResultLabel(_validateAndroidResult);

            if (_settings == null)
            {
                _noSettingsPanel?.RemoveFromClassList("im-hidden");
                _settingsPanel?.AddToClassList("im-hidden");
                return;
            }

            _noSettingsPanel?.AddToClassList("im-hidden");
            _settingsPanel?.RemoveFromClassList("im-hidden");

            var path = AssetDatabase.GetAssetPath(_settings);
            if (_assetPathLabel != null)
                _assetPathLabel.text = string.IsNullOrEmpty(path)
                    ? DaroImLocalization.Get("assetPath.unsaved")
                    : path;

            _serializedSettings = new SerializedObject(_settings);
            rootVisualElement.Bind(_serializedSettings);

            RefreshValidation();
        }

        private void CreateSettingsAsset()
        {
            if (!AssetDatabase.IsValidFolder(CreateDir))
            {
                Directory.CreateDirectory(Path.Combine(
                    Path.GetDirectoryName(Application.dataPath) ?? "", CreateDir));
                AssetDatabase.Refresh();
            }

            if (AssetDatabase.LoadAssetAtPath<DaroSettings>(CreatePath) == null)
            {
                var asset = ScriptableObject.CreateInstance<DaroSettings>();
                AssetDatabase.CreateAsset(asset, CreatePath);
                AssetDatabase.SaveAssets();
            }

            var loaded = AssetDatabase.LoadAssetAtPath<DaroSettings>(CreatePath);
            if (loaded != null)
            {
                DaroSettingsLocator.Register(loaded);
            }
            BindOrShowNoSettings();
        }

        private void RefreshValidation()
        {
            if (_validationList == null) return;
            _validationList.Clear();

            UpdateAiHelperNotice();

            if (_settings == null) return;

            var results = DaroSettingsValidator.Validate(
                _settings, EditorUserBuildSettings.activeBuildTarget);
            var rows = DaroValidationRowFactory.Build(results);

            foreach (var row in rows)
            {
                _validationList.Add(BuildRowElement(row));
            }
        }

        // Renders one DaroValidationRowFactory.Row to a VisualElement tree
        // matching the USS classes (im-validation-row / -dot / -checkid /
        // -message / -hint). Message + FixHint go through DaroImLocalization
        // so the rendered text follows the current language; CheckId stays
        // as-is (stable identifier).
        private VisualElement BuildRowElement(DaroValidationRowFactory.Row row)
        {
            var (msg, hint) = LocalizeRow(row);

            var container = new VisualElement();
            container.AddToClassList("im-validation-row");

            var dot = new VisualElement();
            dot.AddToClassList("im-validation-dot");
            dot.AddToClassList(row.DotClass);
            container.Add(dot);

            var text = new VisualElement();
            text.AddToClassList("im-validation-row-text");

            var top = new VisualElement();
            top.style.flexDirection = FlexDirection.Row;
            top.style.alignItems = Align.Center;

            var checkIdLabel = new Label(row.CheckId);
            checkIdLabel.AddToClassList("im-validation-checkid");
            top.Add(checkIdLabel);

            var msgLabel = new Label(msg);
            msgLabel.AddToClassList("im-validation-message");
            top.Add(msgLabel);

            text.Add(top);

            if (!string.IsNullOrEmpty(hint))
            {
                var hintLabel = new Label(hint);
                hintLabel.AddToClassList("im-validation-hint");
                text.Add(hintLabel);
            }

            container.Add(text);
            return container;
        }

        // Looks up localized templates by CheckId. For the two dynamic checks
        // (any.resourcesPath, any.mediation) the template includes {0}; we
        // re-derive the value from live DaroSettings instead of round-tripping
        // it through ValidationResult, so the validator stays free of locale
        // concerns. Falls back to the validator's own (English) message when
        // a CheckId has no localized template registered.
        private (string msg, string hint) LocalizeRow(DaroValidationRowFactory.Row row)
        {
            var msgKey  = "v." + row.CheckId + ".msg";
            var hintKey = "v." + row.CheckId + ".hint";

            var msgTemplate  = DaroImLocalization.Get(msgKey);
            var hintTemplate = DaroImLocalization.Get(hintKey);

            string msg = msgTemplate == msgKey
                ? row.Message
                : FormatMessage(row.CheckId, msgTemplate);

            string hint = hintTemplate == hintKey
                ? row.FixHint
                : hintTemplate;

            return (msg, hint);
        }

        private string FormatMessage(string checkId, string template)
        {
            if (_settings == null) return template;
            switch (checkId)
            {
                case "any.resourcesPath":
                    return string.Format(template, AssetDatabase.GetAssetPath(_settings));
                case "any.mediation":
                    return string.Format(template, _settings.mediation);
                default:
                    return template;
            }
        }

        // AssetDatabase.FindAssets resolves UPM-package-relative paths the
        // same way as Assets/. Filter by both name and type-string so the
        // search doesn't pick up an unrelated asset that shares the name.
        private static T LoadAsset<T>(string assetName, string typeString) where T : UnityEngine.Object
        {
            var guids = AssetDatabase.FindAssets($"{assetName} t:{typeString}");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<T>(path);
                if (asset != null) return asset;
            }
            return null;
        }
    }
}
