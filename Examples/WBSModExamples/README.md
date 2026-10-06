# 官方模组示例

这个 Unity 工程包含四个可分别打包的模组。选择与你想制作的内容相近的示例，先了解它如何运行，再通过新建向导创建自己的模组。

| 示例 | 内容与阅读入口 |
| --- | --- |
| [人物模型](Assets/Mod/WBSExampleCharacter/README.md) | 美术源模型、人物制作配方、材质与自动登记 |
| [玩法：石头剪刀布](Assets/Mod/WBSExampleGameplay/README.md) | 双人回合逻辑、设置、地图、计分与 Pad 页面 |
| [物品：联网计数器](Assets/Mod/WBSExampleItem/README.md) | 可拾取物品、使用授权、网络同步及测试玩法 |
| [界面](Assets/Mod/WBSExampleUI/README.md) | 管理页主界面、主动打开页面、弹窗与覆盖界面 |

## 打开工程

从 [Releases](https://github.com/MrAIMStein/WBS-MOD-SDK-PUBLIC/releases) 下载自带完整 SDK 的示例 ZIP，解压后用配套 Unity 版本打开 `WBSModExamples`。如果使用本仓库中的源码工程，还需要通过 Package Manager 安装匹配的完整 SDK `.tgz`。环境与安装步骤见 [SDK 安装](../../Documentation/SDK安装.md)。

等待资源导入和编译完成，确认 Console 没有错误，再切换 Windows x64。公共模板、字体、Shader 和运行时 DLL 由 SDK 提供，四个模组各自维护资源、脚本和打包配置。

## 体验一个示例

1. 打开 `WBS/模组/打包工具`。
2. 选择对应模组的 `Assets/Mod/{KeyName}/Editor/Build.asset`，点击“检查”。
3. 用“选择 exe”指定游戏 `WBS.exe`，关闭正在运行的游戏，点击“打包并启动调试”。
4. 根据该示例 README 在游戏中打开相应内容，修改后重新打包并启动。

人物示例还提供 `Editor/Recipe.asset`，可在 `WBS/角色/角色制作工具` 中查看模型、材质和人物参数。配方修改后，先生成并保存完整人物，再打包。

详细调试与正式发布步骤见 [制作与打包](../../Documentation/使用说明.md)。

## 从示例开始做自己的模组

用 `WBS/模组/新建模组` 创建新的 KeyName 和 UUID，再迁入需要的代码或美术内容，并同步代码命名空间、资源地址和打包配置。已经发布的模组更新时保留原身份；官方示例身份用于学习。

人物模型素材有独立的署名与使用要求，复制或发布前阅读 [许可与素材署名](../../THIRD_PARTY_NOTICES.md)。接口用法见 [API 参考](../../Documentation/接口协议.md)，游戏更新后的处理见 [升级与游戏更新](../../Documentation/升级与游戏更新.md)。
