#nullable enable

using UnityEngine;

namespace Daro.Internal
{
    internal static class DaroEditorMockProbability
    {
        internal static bool RollSuccess(float successRate)
        {
            if (successRate <= 0f) return false;
            if (successRate >= 1f) return true;

            return Random.value < successRate;
        }
    }
}
