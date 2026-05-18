namespace Daro.Editor
{
    // Single source of truth for the AI Integration Helper reconcile
    // sequence. Both entry points — `DaroAiKbBootstrap` (Editor boot via
    // `[InitializeOnLoad]`) and `DaroIntegrationManagerWindow.WireAiHelper`
    // (toggle ChangeEvent) — delegate here so future axis additions, log
    // tweaks, or ordering changes happen in one place.
    //
    // Sequence (in order):
    //   1. Legacy CLAUDE.md marker sweep — unconditional. Deprecates the
    //      prior sprint's root-CLAUDE.md inject regardless of toggle state.
    //   2. If toggle is OFF or no AI agent env signal is present →
    //      CleanAll (3 layers cleared; vendor-ownership marker / sentinel
    //      preserve user-authored files at the same paths).
    //   3. Else: KB copy Apply → AGENTS.md marker Apply (D8: exists-only)
    //      → env-signaled own-file Apply, non-signaled own-file defensive
    //      Clean (covers tools the user has since removed).
    internal static class DaroAiKbReconciler
    {
        internal static void ReconcileSync(bool toggleOn)
        {
            foreach (var path in DaroAiKbTargets.LegacyMarkerPaths())
                DaroAiKbInjector.Clean(path);

            if (!toggleOn || !DaroAiKbTargets.AnyEnvSignal())
            {
                CleanAll();
                return;
            }

            DaroAiKbKbCopier.Apply();

            foreach (var path in DaroAiKbTargets.MarkerExistingPaths())
                DaroAiKbInjector.Apply(path, DaroAiKbPayload.DirectiveBlock);

            var root = DaroProjectRoot.Path;
            foreach (var target in DaroAiKbTargets.OwnFileTargets)
            {
                if (target.EnvSignal == null || !target.EnvSignal(root))
                {
                    DaroAiKbOwnFileWriter.Clean(target.AbsolutePath);
                    continue;
                }
                var conflict = target.ConflictGuard?.Invoke(root);
                var body = target.BodyComposer(DaroAiKbPayload.DirectiveBlock);
                DaroAiKbOwnFileWriter.Apply(target.AbsolutePath, body, conflict);
            }
        }

        internal static void CleanAll()
        {
            foreach (var path in DaroAiKbTargets.MarkerAllPaths())
                DaroAiKbInjector.Clean(path);
            foreach (var target in DaroAiKbTargets.OwnFileTargets)
                DaroAiKbOwnFileWriter.Clean(target.AbsolutePath);
            DaroAiKbKbCopier.Clean();
        }
    }
}
