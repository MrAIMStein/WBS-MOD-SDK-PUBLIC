# 导入 VRM 人物模型

本页用于将 VRM 准备成人物制作工具的源模型。完成导入后，还需要生成游戏人物 Prefab，再进行打包；直接把 VRM 文件放进发布目录不会登记人物。

FBX 或已经准备好的模型 Prefab 可以直接按 [制作与打包指南](使用说明.md) 操作，无需安装 VRM 支持。

## 安装可选依赖

在作者工程中安装官方 VRM 1 包和 glTF 包。打开 `Window/Package Manager`，点击 `+` → `Add package from git URL`，依次添加以下两个地址：

| 包 | 固定版本 Git 地址 |
| --- | --- |
| `com.vrmc.gltf` | `https://github.com/vrm-c/UniVRM.git?path=/Packages/UniGLTF#v0.131.2` |
| `com.vrmc.vrm` | `https://github.com/vrm-c/UniVRM.git?path=/Packages/VRM10#v0.131.2` |

地址和安装方式见 [UniVRM 0.131.2 官方说明](https://github.com/vrm-c/UniVRM/releases/tag/v0.131.2)。工程 URP 使用 SDK 配套版本，等待依赖安装完成后再使用 SDK 的 VRM 导入扩展。

使用配套版本。准备升级 UniVRM 时，先查看 SDK 版本说明是否支持目标版本；如果 Console 报依赖或编译错误，先修复安装，再导入模型。

## 导入模型

1. 将模型和许可资料放入自己的模组目录，例如 `Assets/Mod/MyMod/Art/Models/`。保留已导入资源的 `.meta`。
2. 在 Project 窗口选中 `.vrm` 文件，执行 `WBS/模组 SDK/VRM/使用 SDK 导入选中模型`。已有文件无需改名，该操作保留源资源 GUID 和已有导入配置。
3. 如果工程只安装了 VRM 1 官方包，而源文件是 VRM 0，在导入 Inspector 中启用 `MigrateToVrm1` 并应用。已有的 VRM 0 提取 Prefab 可以继续作为源模型使用。
4. URP 工程在导入设置中选择 `UniversalRenderPipeline`，或确认 `Auto` 检测到了工程使用的 URP 配置。
5. 等待导入完成，检查 Console、网格、骨骼、Renderer、Humanoid Avatar 和材质引用。确认模型可以展开，且没有缺失脚本或材质。
6. 打开 `WBS/角色/角色制作工具`，将导入的模型或准备好的源 Prefab 设为配方的源模型，再按 [制作与打包指南](使用说明.md) 生成完整人物。

新文件也可以使用 `.wbsvrm` 扩展名，文件内容仍为 VRM，Unity 会自动选择 SDK 导入器。已有 `.vrm` 文件不必为此改扩展名。

## 材质与行为

VRM 导入使用官方材质生成器和配套 Shader。人物制作工具可以为源材质建立 `WBS/Toon` 独立副本，并将副本和映射保存在你的模组目录及配方中。更新 SDK 不会覆盖作者材质；发布前仍应在游戏中检查光照、透明、裁切和描边。

生成游戏人物时，不会复制 VRM 的表情、LookAt、弹簧和第一人称行为脚本。如果人物需要这些行为，需要另行实现与游戏兼容的逻辑。源模型应只提供人物美术与骨骼，不要把已经生成的完整游戏人物再次当作源模型。

使用他人的 VRM 前，应确认模型许可允许你的用途，并保留需要的署名。官方人物示例的许可见 [第三方与素材声明](https://github.com/MrAIMStein/WBS-MOD-SDK-PUBLIC/blob/main/THIRD_PARTY_NOTICES.md)。SDK 更新后的处理方式见 [游戏更新与模组升级](升级与游戏更新.md)。
