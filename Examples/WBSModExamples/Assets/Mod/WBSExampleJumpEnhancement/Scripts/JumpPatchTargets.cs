using System;
using System.Reflection;
using UnityEngine;
using WBS.Client.Logic.Gameplay.Character;

namespace WBS.Client.Mod.WBSExampleJumpEnhancement
{
    /// <summary>安装前一次性校验全部方法与字段，避免升级后只装上部分补丁。</summary>
    internal static class JumpPatchTargets
    {
        private const BindingFlags DeclaredInstance = BindingFlags.Public | BindingFlags.NonPublic
            | BindingFlags.Instance | BindingFlags.DeclaredOnly;

        internal static MethodInfo FirstJump => Method(typeof(FirstPersonController), "Jump");
        internal static MethodInfo ThirdJump => Method(typeof(ThirdPersonController), "Jump");

        internal static void Validate()
        {
            ValidateCommon(typeof(FirstPersonController));
            ValidateCommon(typeof(ThirdPersonController));
            Field(typeof(FirstPersonController), "effectiveCrouch", typeof(bool));
            Field(typeof(FirstPersonController), "playerPosture", typeof(FirstPersonController.PlayerPosture));
            Field(typeof(ThirdPersonController), "isCrouch", typeof(bool));
            Field(typeof(ThirdPersonController), "playerPosture", typeof(ThirdPersonController.PlayerPosture));
            Method(typeof(FirstPersonController), "Jump");
            Method(typeof(ThirdPersonController), "Jump");
            Method(typeof(ThirdPersonController), "CheckGround");
        }

        private static void ValidateCommon(Type type)
        {
            Field(type, "maxHeight", typeof(float));
            Field(type, "gravity", typeof(float));
            Field(type, "VerticalVelocity", typeof(float));
            Field(type, "isJumping", typeof(bool));
            Field(type, "isGrounded", typeof(bool));
            Field(type, "isLanding", typeof(bool));
            Field(type, "couldFall", typeof(bool));
            Field(type, "characterController", typeof(CharacterController));
        }

        private static void Field(Type type, string name, Type expected)
        {
            FieldInfo field = type.GetField(name, DeclaredInstance);
            if (field == null || field.FieldType != expected || field.IsInitOnly)
                throw new MissingFieldException("跳跃增强示例与当前游戏不兼容：" + type.FullName + "." + name
                    + " 必须为可写的 " + expected.FullName + " 字段。");
        }

        private static MethodInfo Method(Type type, string name)
        {
            MethodInfo method = type.GetMethod(name, DeclaredInstance, null, Type.EmptyTypes, null);
            if (method == null || method.ReturnType != typeof(void) || method.IsGenericMethod)
                throw new MissingMethodException("跳跃增强示例与当前游戏不兼容：缺少 void " + type.FullName + "." + name + "()。");
            return method;
        }
    }
}
