# 智斗模拟器模组开发工具

使用 Unity 为《智斗模拟器》（Wits Battle Simulator）制作人物、玩法、物品和界面模组。SDK 提供人物制作器、新建模组向导和打包工具，帮助你把作者工程中的内容制作为玩家可以安装的模组。

> SDK / 模组 API `2.1.0` 已提供[预发布下载](https://github.com/MrAIMStein/WBS-MOD-SDK-PUBLIC/releases/tag/v2.1.0)，包含完整 SDK、配套五示例作者工程和跳跃增强模组成品。使用 Unity `2022.3.62f3`，目标游戏需支持 Mod API `2.1.0`。正式 Player 的模组加载与补丁清理已验证；正常游戏的跳跃操作矩阵和双 Steam 客户端同步仍待验收，具体范围见发布说明。

## 从哪里开始

首次制作推荐使用 Release 中自带 SDK 的示例工程。人物模组可通过制作工具完成；玩法、物品和交互界面需要基础 Unity 与 C# 知识。

1. [安装 SDK](Documentation/SDK安装.md)，或打开配套示例工程。
2. 通过 `WBS/模组/新建模组` 创建自己的模组，再按 [制作与打包](Documentation/使用说明.md) 添加内容。
3. 用打包工具检查并生成发布目录，在游戏中调试。
4. 将完整成品目录分享给玩家，或通过游戏上传至 Steam 创意工坊。

## 选择一个示例

| 示例 | 你可以学到 | 说明 |
| --- | --- | --- |
| 人物模型 | 导入模型、调整人物、制作材质、自动登记人物 | [WBSExampleCharacter](Examples/WBSModExamples/Assets/Mod/WBSExampleCharacter/README.md) |
| 玩法：石头剪刀布 | 设置、地图、回合逻辑、联机同步和 Pad 页面 | [WBSExampleGameplay](Examples/WBSModExamples/Assets/Mod/WBSExampleGameplay/README.md) |
| 物品：联网计数器 | 物品登记、使用逻辑、持有者检查和联网显示 | [WBSExampleItem](Examples/WBSModExamples/Assets/Mod/WBSExampleItem/README.md) |
| 界面 | 登记模组主界面，打开页面、弹窗和覆盖界面 | [WBSExampleUI](Examples/WBSModExamples/Assets/Mod/WBSExampleUI/README.md) |
| 跳跃增强 | 原生 Harmony API、提高跳跃高度、多段跳跃与补丁清理 | [WBSExampleJumpEnhancement](Examples/WBSModExamples/Assets/Mod/WBSExampleJumpEnhancement/README.md) |

五个示例位于同一 [示例工程](Examples/WBSModExamples/README.md)，可以分别打包。跳跃增强是一个同时包含高度增强和多段跳跃的模组，参数在源码中集中设置。制作自己的模组时使用新建向导生成的 UUID 和 KeyName，保留官方示例身份供学习。

## 按需要查阅

| 内容 | 文档 |
| --- | --- |
| 安装环境、下载与打开工程 | [SDK 安装](Documentation/SDK安装.md) |
| 从创建到制作、调试、打包和分享 | [制作与打包](Documentation/使用说明.md) |
| 入口、资源、人物、玩法、物品、UI、Harmony 与生命周期 | [API 参考](Documentation/接口协议.md) |
| 游戏更新后是否重打，如何升级 SDK 和人物模板 | [升级与游戏更新](Documentation/升级与游戏更新.md) |
| 使用 VRM 模型制作人物 | [VRM 模型导入](Documentation/VRM模型导入.md) |
| 可随模组成品提供给玩家的安装说明 | [交给玩家安装](Documentation/使用说明.md#交给玩家安装) |
| 使用和分享示例代码、模型的授权范围 | [许可与素材署名](THIRD_PARTY_NOTICES.md) |

`Packages/com.wbs.mod-editor` 用于阅读和修改 SDK 编辑器工具源码。安装开发环境时使用 Release 提供的完整 `com.wbs.mod-sdk` 包，它还包含运行时 DLL、公共人物模板、动画、Shader 和必要依赖。

SDK 和游戏分别维护版本；兼容的游戏更新可以继续使用同一 SDK 与模组成品。自研工具、示例代码和说明使用 [MIT 许可证](LICENSE)，素材及第三方内容遵循各自许可。
