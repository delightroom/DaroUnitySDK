using System.Collections.Generic;

namespace Daro.Editor
{
    // Pure compute layer for the IM window's validation panel — converts
    // ValidationResult records (DaroSettingsValidator output) into row data
    // that the UI Toolkit layer can render directly.
    //
    // Splitting this out is the IM window's *only* testable seam (the rest
    // of the window is UI Toolkit / SerializedObject binding which doesn't
    // sit cleanly under EditMode tests). Regression-protecting the row
    // shape keeps future row-format changes from silently breaking the
    // panel rendering.
    internal static class DaroValidationRowFactory
    {
        // CSS class names mirror the USS in DaroIntegrationManagerWindow.uss.
        // Keep in sync with that file — these strings are the contract.
        internal const string DotPass = "im-validation-dot--pass";
        internal const string DotWarn = "im-validation-dot--warn";
        internal const string DotFail = "im-validation-dot--fail";

        internal readonly struct Row
        {
            internal readonly ValidationSeverity Severity;
            internal readonly string DotClass;
            internal readonly string CheckId;
            internal readonly string Message;
            internal readonly string FixHint;
            internal readonly bool HasFixHint;

            internal Row(ValidationSeverity severity, string dotClass, string checkId,
                         string message, string fixHint)
            {
                Severity = severity;
                DotClass = dotClass;
                CheckId = checkId;
                Message = message;
                FixHint = fixHint;
                HasFixHint = !string.IsNullOrEmpty(fixHint);
            }
        }

        internal static string DotClassFor(ValidationSeverity severity) => severity switch
        {
            ValidationSeverity.Pass => DotPass,
            ValidationSeverity.Warn => DotWarn,
            ValidationSeverity.Fail => DotFail,
            _ => DotFail,
        };

        internal static Row[] Build(IReadOnlyList<ValidationResult> results)
        {
            if (results == null || results.Count == 0)
                return System.Array.Empty<Row>();

            var rows = new Row[results.Count];
            for (var i = 0; i < results.Count; i++)
            {
                var r = results[i];
                rows[i] = new Row(
                    r.Severity,
                    DotClassFor(r.Severity),
                    r.CheckId,
                    r.Message,
                    r.FixHint);
            }
            return rows;
        }
    }
}
