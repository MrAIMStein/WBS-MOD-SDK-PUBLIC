using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using WBS.Client.Common.InputControl;
using WBS.Client.Logic.Gameplay.Character;

namespace WBS.Client.Mod.WBSExampleJumpEnhancement
{
    /// <summary>同一个原生补丁类覆盖两种控制器；所有 Postfix 完成后才还原临时高度。</summary>
    [HarmonyPatch]
    internal static class JumpHeightPatch
    {
        internal struct HeightSnapshot
        {
            internal bool Changed;
            internal float Original;
        }

        [HarmonyTargetMethods]
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return JumpPatchTargets.FirstJump;
            yield return JumpPatchTargets.ThirdJump;
        }

        [HarmonyPrefix]
        private static void Prefix(object __instance, ref float ___maxHeight, out HeightSnapshot __state)
        {
            __state = default;
            if (!JumpEnhancementState.TryGet(__instance, out var owner, out _) || owner.HeightMultiplier == 1f) return;
            __state = new HeightSnapshot { Changed = true, Original = ___maxHeight };
            ___maxHeight *= owner.HeightMultiplier;
        }

        [HarmonyFinalizer]
        private static void Finalizer(ref float ___maxHeight, HeightSnapshot __state)
        {
            if (__state.Changed) ___maxHeight = __state.Original;
        }
    }

    internal struct JumpInputSnapshot
    {
        internal bool Changed;
        internal bool OriginalInput;
        internal bool OnGround;
        internal bool AirJumpRequested;
        internal bool GroundJumpRequested;
        internal JumpEnhancementState Owner;
        internal CharacterJumpState Character;
    }

    internal static class MultiJumpPreparation
    {
        internal static JumpInputSnapshot Prepare(object controller, float verticalVelocity, bool crouching,
            bool freshPress, ref bool jumpInput, bool? knownActualGrounded = null)
        {
            if (!JumpEnhancementState.TryGet(controller, out var owner, out var capsule) || owner.MaxJumpCount == 1)
                return default;
            CharacterJumpState character = owner.For(capsule);
            bool onGround = knownActualGrounded.HasValue
                ? verticalVelocity <= 0f && knownActualGrounded.Value
                : JumpEnhancementState.IsActuallyGrounded(capsule, verticalVelocity);
            if (onGround) character.AirJumpsUsed = 0;
            bool newJump = jumpInput && freshPress && !crouching && character.LastJumpFrame != Time.frameCount;
            var snapshot = new JumpInputSnapshot
            {
                Changed = true, OriginalInput = jumpInput, OnGround = onGround,
                AirJumpRequested = !onGround && newJump && character.AirJumpsUsed < owner.MaxJumpCount - 1,
                GroundJumpRequested = onGround && newJump, Owner = owner, Character = character,
            };
            // 第三人称原生 Jump 没有着地门槛；空中一律交给 Postfix，避免小落差绕过额度。
            jumpInput = snapshot.GroundJumpRequested;
            return snapshot;
        }

        internal static bool ApplyAirJump(JumpInputSnapshot snapshot, bool runOriginal,
            float gravity, float height, ref float velocity, ref bool grounded, ref bool landing)
        {
            if (!snapshot.Changed || !runOriginal || !snapshot.Owner.IsAvailable) return false;
            if (snapshot.OnGround)
            {
                if (snapshot.GroundJumpRequested && velocity > 0f) snapshot.Character.LastJumpFrame = Time.frameCount;
                return false;
            }
            if (!snapshot.AirJumpRequested) return false;
            velocity = Mathf.Sqrt(-2f * gravity * height);
            grounded = false;
            landing = false;
            snapshot.Character.AirJumpsUsed++;
            snapshot.Character.LastJumpFrame = Time.frameCount;
            return true;
        }
    }

    [HarmonyPatch(typeof(FirstPersonController), "Jump")]
    internal static class FirstPersonMultiJumpPatch
    {
        [HarmonyPrefix]
        private static void Prefix(FirstPersonController __instance, float ___VerticalVelocity,
            bool ___isGrounded, bool ___effectiveCrouch, ref bool ___isJumping, out JumpInputSnapshot __state)
        {
            // 第一人称原生字段已经是按下沿，也能保留宿主工具提供的单次输入。
            bool freshPress = ___isJumping && !InputFocusHelper.IsTypingInInputField();
            // 本体在 Jump 前从真实 Move 缓存更新接地，避免蹲伏尺寸重建使胶囊接触信息失效。
            __state = MultiJumpPreparation.Prepare(__instance, ___VerticalVelocity, ___effectiveCrouch,
                freshPress, ref ___isJumping, ___isGrounded);
        }

        [HarmonyPostfix]
        private static void Postfix(FirstPersonController __instance, bool __runOriginal, float ___gravity,
            float ___maxHeight, ref float ___VerticalVelocity, ref bool ___isGrounded, ref bool ___isLanding, JumpInputSnapshot __state)
        {
            if (MultiJumpPreparation.ApplyAirJump(__state, __runOriginal, ___gravity, ___maxHeight,
                ref ___VerticalVelocity, ref ___isGrounded, ref ___isLanding))
                __instance.playerPosture = FirstPersonController.PlayerPosture.Jumping;
        }

        [HarmonyFinalizer]
        private static void Finalizer(ref bool ___isJumping, JumpInputSnapshot __state)
        {
            if (__state.Changed) ___isJumping = __state.OriginalInput;
        }
    }

    [HarmonyPatch(typeof(ThirdPersonController), "Jump")]
    internal static class ThirdPersonMultiJumpPatch
    {
        [HarmonyPrefix]
        private static void Prefix(ThirdPersonController __instance, float ___VerticalVelocity,
            bool ___isCrouch, ref bool ___isJumping, out JumpInputSnapshot __state)
        {
            // 第三人称原生字段是持续按住，必须额外检查按下沿。
            bool freshPress = ___isJumping && PlayerInputController.actions != null
                && PlayerInputController.actions.ThirdPersonController.Jump.WasPressedThisFrame()
                && !InputFocusHelper.IsTypingInInputField();
            __state = MultiJumpPreparation.Prepare(__instance, ___VerticalVelocity, ___isCrouch, freshPress, ref ___isJumping);
        }

        [HarmonyPostfix]
        private static void Postfix(ThirdPersonController __instance, bool __runOriginal, float ___gravity,
            float ___maxHeight, ref float ___VerticalVelocity, ref bool ___isGrounded, ref bool ___isLanding, JumpInputSnapshot __state)
        {
            if (MultiJumpPreparation.ApplyAirJump(__state, __runOriginal, ___gravity, ___maxHeight,
                ref ___VerticalVelocity, ref ___isGrounded, ref ___isLanding))
                __instance.playerPosture = ThirdPersonController.PlayerPosture.Jumping;
        }

        [HarmonyFinalizer]
        private static void Finalizer(ref bool ___isJumping, JumpInputSnapshot __state)
        {
            if (__state.Changed) ___isJumping = __state.OriginalInput;
        }
    }

    [HarmonyPatch(typeof(ThirdPersonController), "CheckGround")]
    internal static class ThirdPersonRisingGroundPatch
    {
        [HarmonyPostfix]
        private static void Postfix(ThirdPersonController __instance, float ___VerticalVelocity,
            ref bool ___isGrounded, ref bool ___couldFall)
        {
            if (!JumpEnhancementState.TryGet(__instance, out _, out _) || ___VerticalVelocity <= 0f) return;
            // 起跳初期探针仍会碰到地面；先排除上升，原状态机和重力才能继续工作。
            ___isGrounded = false;
            ___couldFall = false;
        }
    }
}
