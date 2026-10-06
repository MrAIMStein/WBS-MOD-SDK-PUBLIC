# 安装 SDK

SDK 安装在你的 Unity 作者工程中。开始前先选择 [Release](https://github.com/MrAIMStein/WBS-MOD-SDK-PUBLIC/releases) 所提供的完整 SDK 或配套示例工程，并阅读该版本的环境要求。

## 选择下载内容

| 你的目标 | 下载内容 |
| --- | --- |
| 先运行并学习官方示例 | 自带 SDK 的示例工程 ZIP |
| 在自己的空白工程中制作 | 完整 `com.wbs.mod-sdk` `.tgz` |
| 阅读工具和示例代码 | 本仓库源码 |

示例 ZIP 已配置同版完整 SDK。GitHub 的源码 ZIP 和 `Packages/com.wbs.mod-editor` 仅提供公开源码；使用源码示例时，还需要安装完整 SDK。

Release 同时提供 SHA256 校验文件，可在下载后核对文件完整性。SDK 版本、示例工程和说明应来自同一 Release。

## 准备 Unity

当前 SDK 配套环境如下。用 Unity Hub 安装对应 Editor，并包含 Windows 构建支持。

| 工具 | 版本 |
| --- | --- |
| Unity Editor | `2022.3.62f3` |
| Addressables | `1.22.3` |
| Universal Render Pipeline（URP） | `14.0.12` |
| Input System | `1.14.0` |
| 模组构建平台 | Windows x64 |

等待安装完成后再打开作者工程。工程中的包依赖使用 SDK 配套版本；升级这些依赖前，先查看目标 SDK 的说明。

## 使用配套示例工程

1. 将示例 ZIP 完整解压到新文件夹，保留附带的 SDK 文件及目录结构。
2. 在 Unity Hub 中添加并打开 `WBSModExamples` 工程。
3. 等待 Package Manager 安装、资源导入和脚本编译完成。
4. 打开 `File/Build Settings`，选择 Windows、x86_64 并切换平台。
5. 确认 Console 没有编译错误，再按示例 README 选择要学习的模组。

## 在空白工程中安装

1. 创建独立的 URP 工程，用于保存你的模组内容。
2. 保存完整 SDK `.tgz`。工程迁移到其他机器时，需要同时保留这个文件及其包引用。
3. 打开 `Window/Package Manager`，点击 `+` → `Add package from tarball`，选择 `com.wbs.mod-sdk` `.tgz`。
4. 等待依赖安装、导入和编译完成，再切换到 Windows x64 构建平台。
5. 确认 Console 没有编译错误，打开 `WBS/模组/新建模组`。

公共模板、动画、Shader、字体、运行时 DLL 与制作工具由 SDK 管理。你的模型、材质、配方、脚本和数据放在 `Assets/Mod/{KeyName}/`，具体操作见 [制作与打包](使用说明.md)。

## 安装遇到问题

| 现象 | 处理方式 |
| --- | --- |
| 缺少 WBS 类型，Prefab 显示缺失脚本 | 确认已安装完整 SDK，且与示例版本匹配；先处理 Console 中的编译错误 |
| 包引用失效，迁移机器后不能打开 | 确认 `.tgz` 仍在引用的位置；通过 Package Manager 重新选择完整包 |
| 找不到 WBS 菜单 | 等待编译完成，检查 Console；编译错误会阻止工具正常加载 |
| 依赖版本或构建平台不符合要求 | 按配套版本恢复依赖，切换 Windows x64 后再检查 |

更新已有工程时，先备份工程和成品，再替换完整 SDK。保留作者资产及 `.meta`，后续步骤见 [升级与游戏更新](升级与游戏更新.md)。
