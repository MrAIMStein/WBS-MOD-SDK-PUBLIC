# 跳跃增强示例

这个示例在一个模组中同时展示跳跃高度和多段跳跃，使用一个 UUID、一个入口程序集和一份打包配置。作者继续使用原生 Harmony 的补丁特性；宿主负责提交时安装补丁、加载失败回滚和卸载清理。

## 打开与验证

按照 [制作与打包说明](../../../../../Documentation/使用说明.md) 打包 `Editor/Build.asset`，安装并启用模组，再进入支持正常人物移动的游戏场景。示例无需模型、地图、UI 或运行时资源。

默认将控制器的 `maxHeight` 参数乘 **2**，允许 **2 次跳跃**：一次地面起跳加一次空中补跳。地面跳起后松开跳跃键，再次按下即可补跳；继续长按不会自动消耗空中次数。落地后恢复次数。走下台阶或悬崖同样只有一次空中补跳。

第一人称和第三人称共用角色的 `CharacterController` 额度，因此切换控制器不会补充次数。幽灵、观察者和使用服务器输入的机器人沿用本体逻辑。

| 文件 | 学习内容 |
| --- | --- |
| [WBSExampleJumpEnhancementMod.cs](Scripts/WBSExampleJumpEnhancementMod.cs) | 两个参数、一次受管理登记和 `Context.Own` |
| [JumpEnhancementPatches.cs](Scripts/JumpEnhancementPatches.cs) | 原生 Prefix、Postfix、Finalizer，以及两项效果的组合 |
| [JumpEnhancementState.cs](Scripts/JumpEnhancementState.cs) | 胶囊共享额度、弱引用缓存和上下文清理 |
| [JumpPatchTargets.cs](Scripts/JumpPatchTargets.cs) | 安装前校验私有方法和字段 |

模组 KeyName 为 `WBSExampleJumpEnhancement`，UUID 为 `ef8b4c5a-b63c-4b76-9962-41c31a245b48`，入口程序集为 `WBSExampleJumpEnhancementModEntry`，最低需要 **Mod API 2.1.0**。

## 受管理登记与原生写法

```csharp
var state = Context.Own(new JumpEnhancementState(Context, HeightMultiplier, MaxJumpCount));
ModApi.For(Context).RegisterHarmonyPatches(harmony =>
{
    state.Activate();
    harmony.PatchAll(typeof(WBSExampleJumpEnhancementMod).Assembly);
});
```

登记回调在模组提交时同步执行。补丁类仍直接使用 `[HarmonyPatch]`、`[HarmonyPrefix]`、`[HarmonyPostfix]` 和 `[HarmonyFinalizer]`；不需要把每个 Harmony 方法再封装一遍。

不要保存收到的 Harmony 实例用于后台任务或卸载后的安装操作。加载失败或上下文释放后，宿主撤销该模组 ID 的 Prefix、Postfix、Transpiler 和 Finalizer；`Context.Own` 同时清理本例的静态指向和角色额度缓存。自动清理不覆盖 ReversePatch，也不会恢复已经变化的玩家位置和游戏数据。作者自行创建实例或在回调外继续安装补丁时，自行负责其生命周期。

## 两项效果如何组合

高度 Prefix 只在原生 `Jump()` 调用期间放大 `maxHeight`，Finalizer 在原生方法与全部 Postfix 结束后恢复，即使其他补丁抛异常也会恢复。多段跳的 Postfix 使用这时仍被放大的高度计算空中起跳速度，两项效果共用一份高度参数。

多段跳 Prefix 保存输入，在空中暂时关闭原生跳跃输入，由 Postfix 在检查额度后补充速度与起跳姿态；Finalizer 恢复原输入。地面跳仍走本体条件和脚部动画，空中补跳保留前一跳的脚部状态。本例不复制整个移动控制器，也不创建新的角色组件。

第三人称地面探针在起跳初期可能仍命中地面，所以另有一个小补丁在上升时排除着地，防止原状态机与重力提前结束跳跃。额度仅在实际接地且垂直速度不为正时恢复。

第一人称使用本体在 `Jump()` 前更新的 `isGrounded`，它来自上一次真实 `CharacterController.Move` 的接地碰撞缓存。蹲伏或恢复站姿改变胶囊尺寸时，直接读取 `CharacterController.isGrounded` 可能丢失接触信息；使用本体缓存可避免把这时的地面跳误计为空中补跳。重新启用第一人称时，本体先清空 Move 缓存，再在 `Jump()` 前更新接地，因此切换控制器不会沿用停用前的旧接地状态。第三人称继续检查胶囊的 `isGrounded`，不把它的近地探针当作实际接地。

## 修改参数与兼容范围

在入口顶部修改 `HeightMultiplier` 和 `MaxJumpCount`，重新打包并安装后重启游戏。倍数必须是大于等于 1 的有限值，次数必须大于等于 1。两个值都为 1 时所有行为补丁直接旁路；倍数为 1 时关闭高度增强，次数为 1 时关闭额外空中跳。

倍数作用于 `maxHeight` 参数，起跳速度按 `sqrt(-2 * gravity * maxHeight)` 计算。重力、松开按键时的重力倍数、帧率和碰撞仍由本体处理，所以它不保证实测最高点严格为原来的两倍。

本例修补的是本体的私有控制器实现，游戏升级可能改变目标。加载前会检查方法签名和字段类型，失配时明确报错；不要在这种情况下静默跳过某一类补丁。与其他修补相同输入、速度、地面或高度字段的模组并用时，需要测试补丁顺序和行为。

制作自己的模组时使用新建向导生成独立 UUID 和 KeyName，再参考本例的结构。接口说明见 [模组 API 参考](../../../../../Documentation/接口协议.md)。
