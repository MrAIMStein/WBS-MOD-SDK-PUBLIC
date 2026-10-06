# 界面示例

这个示例适合学习如何给模组登记一个主界面，再由按钮打开页面、确认弹窗和覆盖层。所有静态控件、布局和引用都保存在作者 Prefab 中。

## 打开示例

按照 [制作与打包说明](../../../../../Documentation/使用说明.md) 打包 `Editor/Build.asset`，安装并启用模组。游戏中进入“模组管理 → 管理”，选中“界面示例”，点击“保存更改”左侧的“面板”按钮。

主界面包含页面、弹窗、覆盖层和关闭按钮：页面显示在页面层；弹窗确认后打开页面并切换覆盖层；覆盖层按钮再次点击时关闭覆盖层。状态文字显示页面打开次数和覆盖层状态。

## 从这些文件开始

| 文件 | 学习内容 |
| --- | --- |
| [WBSExampleUIMod.cs](Scripts/WBSExampleUIMod.cs) | 在 `OnLoad()` 中登记唯一主界面 |
| [ExamplePanel.cs](Scripts/ExamplePanel.cs) | 保存 UI 会话、绑定按钮、打开子界面与关闭清理 |
| `Art/Prefabs/Panel.prefab` | 完整主界面的标题、布局、文本与按钮 |
| `Art/Prefabs/Hud.prefab` | 页面和覆盖层共用的展示资源 |

模组 KeyName 为 `WBSExampleUI`，主界面登记 ID 为 `Example`，入口程序集为 `WBSExampleUIModEntry`。

## 主界面与主动打开

入口只登记主界面，不在加载时打开 UI：

```csharp
var api = ModApi.For(Context);
api.Ui.RegisterMenuPanel("Example", "界面示例", "Art/Prefabs/Panel.prefab");
```

每个模组最多登记一个主界面。它的标题、装饰和关闭按钮由作者制作；未登记主界面也不会阻止模组的其他功能加载。

`ExamplePanel` 实现 `IModUiPanel`，在 `OnModPanelOpened` 取得主会话。按钮通过这份会话打开其他界面：

```csharp
var ui = ModApi.For(owner.Context).Ui;
owner.Own(ui.OpenView("Art/Prefabs/Hud.prefab"));
owner.Own(ui.OpenPopup("提示", "确认执行操作？", () => { /* 作者操作 */ }));

var canvas = GetComponentInParent<Canvas>();
var overlay = ui.OpenPanel("Art/Prefabs/Hud.prefab", canvas.transform,
    useStack: false, useBg: false);
owner.Own(overlay);
```

这段放在已打开面板的组件中使用，`owner` 是收到的 `ModUiSession`。`OpenView` 使用页面层、背景和过渡；`OpenPopup` 使用游戏标准弹窗；覆盖层不入栈、不带背景，布局、显隐和打开时机由作者控制。

`parent` 使用当前屏幕 Canvas 根或它的直接 `Panel/View/Popup/Floating/Message` 层。覆盖层不会自动跟随游戏 HUD 的显隐。Pad 页面、规则页和世界空间 UI 使用各自机制。

## 可以先做的修改

1. 修改 `Panel.prefab` 的外观与布局，保持 `ExamplePanel` 的按钮、文字字段引用。
2. 复制展示 Prefab，分别为页面和覆盖层制作不同布局，再修改对应资源地址。
3. 将按钮中的演示逻辑换成自己的功能，保存需要持续显示的状态。
4. 给覆盖层的非交互图片、文字关闭 `raycastTarget`，避免透明区域挡住游戏按钮。

关闭按钮调用本会话的 `Close()`。示例用主会话的 `Own` 接管子会话，所以关闭主界面时页面、弹窗和覆盖层一起清理。需要独立存在的界面，可以自行保存并关闭其会话。

在 `OnModPanelClosing` 中解除按钮与事件监听；关闭回调只做清理，不立即打开新 UI。场景退出、UI 整体关闭或模组卸载时，会话也会清理。

做自己的界面模组时，通过新建向导取得独立身份，再参考本例的实现。所有接口与参数见 [模组 API 参考](../../../../../Documentation/接口协议.md)。
