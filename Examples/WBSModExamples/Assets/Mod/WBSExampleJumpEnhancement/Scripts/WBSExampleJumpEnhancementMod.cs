using WBS.Client.Common.Mod;
using WBS.Client.Logic.ModSupport;

namespace WBS.Client.Mod.WBSExampleJumpEnhancement
{
    /// <summary>一个入口同时演示原生 Harmony 补丁和模组所属状态的清理。</summary>
    public sealed class WBSExampleJumpEnhancementMod : ModBase
    {
        public const float HeightMultiplier = 2f;
        public const int MaxJumpCount = 2;

        public override bool OnLoad()
        {
            // 私有实现不是稳定 Mod API；游戏升级改变目标时明确加载失败。
            JumpPatchTargets.Validate();
            var state = Context.Own(new JumpEnhancementState(Context, HeightMultiplier, MaxJumpCount));
            ModApi.For(Context).RegisterHarmonyPatches(harmony =>
            {
                state.Activate();
                harmony.PatchAll(typeof(WBSExampleJumpEnhancementMod).Assembly);
            });
            return true;
        }
    }
}
