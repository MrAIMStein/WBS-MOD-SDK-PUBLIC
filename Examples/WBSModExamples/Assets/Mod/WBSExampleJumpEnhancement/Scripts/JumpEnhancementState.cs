using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using WBS.Client.Common.Mod;
using WBS.Client.Logic.Gameplay.Character;

namespace WBS.Client.Mod.WBSExampleJumpEnhancement
{
    /// <summary>额度属于共享胶囊，切换第一/第三人称不会取得新的空中跳。</summary>
    internal sealed class CharacterJumpState
    {
        internal int AirJumpsUsed;
        internal int LastJumpFrame = -1;
    }

    /// <summary>Context.Own 在提交失败和卸载时释放这份状态，防止静态缓存跨加载残留。</summary>
    internal sealed class JumpEnhancementState : IDisposable
    {
        internal static JumpEnhancementState Current { get; private set; }
        private ConditionalWeakTable<CharacterController, CharacterJumpState> characters = new();
        private readonly ModContext context;
        private bool disposed;
        internal float HeightMultiplier { get; }
        internal int MaxJumpCount { get; }
        internal bool HasEnhancements => HeightMultiplier != 1f || MaxJumpCount != 1;

        internal JumpEnhancementState(ModContext context, float heightMultiplier, int maxJumpCount)
        {
            this.context = context ?? throw new ArgumentNullException(nameof(context));
            if (float.IsNaN(heightMultiplier) || float.IsInfinity(heightMultiplier) || heightMultiplier < 1f)
                throw new ArgumentOutOfRangeException(nameof(heightMultiplier), "跳跃高度倍数必须为不小于 1 的有限数。");
            if (maxJumpCount < 1) throw new ArgumentOutOfRangeException(nameof(maxJumpCount), "最大跳跃次数至少为 1。");
            HeightMultiplier = heightMultiplier;
            MaxJumpCount = maxJumpCount;
        }

        internal void Activate()
        {
            context.ThrowIfUnavailable();
            if (disposed) throw new ObjectDisposedException(nameof(JumpEnhancementState));
            if (Current != null && !ReferenceEquals(Current, this))
                throw new InvalidOperationException("跳跃增强示例已经由另一个模组上下文启用。");
            Current = this;
        }

        internal CharacterJumpState For(CharacterController capsule) =>
            characters.GetValue(capsule, _ => new CharacterJumpState());

        internal static bool TryGet(object instance, out JumpEnhancementState owner, out CharacterController capsule)
        {
            owner = Current;
            capsule = null;
            if (owner == null || owner.disposed || owner.context.State == ModContextState.Disposed || !owner.HasEnhancements)
                return false;
            var controller = instance as MonoBehaviour;
            if (controller == null) return false;
            // 服务器机器人的输入由服务器提供，本例只改变本机玩家输入。
            if (controller is FirstPersonController first && first.UsesServerInput) return false;
            if (controller.GetComponent<GhostVisualController>() != null || controller.GetComponent<ObserverVisualController>() != null)
                return false;
            capsule = controller.GetComponent<CharacterController>();
            return capsule != null && capsule.enabled;
        }

        internal static bool IsActuallyGrounded(CharacterController capsule, float verticalVelocity) =>
            verticalVelocity <= 0f && capsule.isGrounded;

        internal bool IsAvailable => !disposed && context.State != ModContextState.Disposed;

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            // 旧上下文的清理不能清掉后来启用的新上下文。
            if (ReferenceEquals(Current, this)) Current = null;
            characters = new ConditionalWeakTable<CharacterController, CharacterJumpState>();
        }
    }
}
