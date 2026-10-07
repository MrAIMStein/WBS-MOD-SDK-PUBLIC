# 许可与素材署名

制作和分享模组时，代码、模型、贴图、字体和第三方库可能采用不同许可。请按你实际使用的内容保留相应声明。

## 自研工具、代码与文档

本仓库自研编辑器工具、示例 C# 代码和自研说明文档采用 MIT 许可。你可以使用、修改和分享这些内容，并保留版权与许可声明。完整许可文本随仓库提供，标准条款见 [MIT License](https://spdx.org/licenses/MIT.html)。

人物美术、游戏共享资源、运行时 DLL 和第三方依赖仍遵循各自许可。代码的 MIT 许可不会重新授权这些内容。

## 人物示例素材

| 项目 | 来源 |
| --- | --- |
| 模型名称 | 유리아（Yuria） |
| 模型作者 | BwingB |
| 导出工具 | VRoid Studio `1.26.0` |
| 上游工程 | [WBSTest1002Mod 固定版本](https://github.com/MrAIMStein/WBSTest1002Mod/tree/29bb7abe083f116da0aeec976084f405ff572910) |
| 原始作者与许可记录 | [模型 VRM Meta](https://github.com/MrAIMStein/WBSTest1002Mod/blob/29bb7abe083f116da0aeec976084f405ff572910/Assets/Mod/Test1002/Art/Models/Character/vrm_m3/m3.MetaObject/Meta.asset) |

模型、派生成品、网格、材质和贴图按 [VRoid Hub 原许可](https://hub.vroid.com/license?allowed_to_use_user=everyone&characterization_allowed_user=everyone&corporate_commercial_use=allow&credit=necessary&modification=allow&personal_commercial_use=profit&redistribution=allow&sexual_expression=disallow&version=1&violent_expression=disallow) 使用。原记录要求署名，并禁止暴力和性表达；修改、商用及再分发的具体范围按原许可核对。示例提供模型制作方法，模型的授权范围保持原样。

使用这些素材制作或分享模组时，保留原作者、来源和许可链接。可以在模组说明中使用以下署名：

> 模型：유리아（Yuria），作者：BwingB；由 VRoid Studio 1.26.0 导出。美术源来自 WBSTest1002Mod，使用范围以原 VRoid Hub 许可为准。

换成自己的模型后，填写该模型真实的来源、作者及使用许可，并更新人物制作配方和发布说明。

## 游戏资源与第三方依赖

### Harmony

完整 SDK 显式提供游戏宿主已有的 `0Harmony.dll`，程序集身份为 `0Harmony, Version=2.2.2.0, Culture=neutral, PublicKeyToken=null`，没有更换上游版本。固定来源为 [Harmony v2.2.2.0](https://github.com/pardeike/Harmony/tree/v2.2.2.0)，原许可为 [MIT](https://github.com/pardeike/Harmony/blob/v2.2.2.0/LICENSE)。程序集字节 SHA256 随 SDK 冻结清单记录。

作者引用配套 SDK 中的 Harmony，模组打包时排除该宿主 DLL。若在其他允许的用途重新分发 Harmony，须保留以下原版权和许可声明。完整 SDK 同时带有 `Shared/c9f8b150/Harmony-LICENSE.txt`，其来源、字节哈希与 DLL／导入器身份写入 `sdk-release.json` 的 `harmony` 记录。

```text
MIT License

Copyright (c) 2017 Andreas Pardeike

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

完整 SDK 中的游戏运行时 DLL、公共人物模板、动画、Shader 和共享工具按其提供的授权使用。Unity、Addressables、URP、Input System、TextMeshPro、Mirror、UniVRM 等保留各自项目或软件包的许可。

公开源码仓库中的自研代码许可与完整 SDK 中的资源许可分别适用。分发模组时保留所使用的第三方许可、字体声明和素材署名；打包器也会排除 SDK 与游戏宿主 DLL。
