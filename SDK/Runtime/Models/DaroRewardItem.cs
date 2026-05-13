#nullable enable

namespace Daro
{
    /// <summary>
    /// Delivered with <c>DaroRewardedAd.OnEarnedReward</c>.
    /// <see cref="RewardType"/> property matches DaroSDK's iOS
    /// <c>DaroObjCRewardedItem.rewardType</c> field spelling.
    /// Android's Kotlin field is named <c>type</c>; the Android shim
    /// normalizes to this property at the bridge boundary.
    /// </summary>
    public sealed class DaroRewardItem
    {
        public int    Amount     { get; }
        public string RewardType { get; }

        public DaroRewardItem(int amount, string rewardType)
        {
            Amount     = amount;
            RewardType = rewardType;
        }
    }
}
