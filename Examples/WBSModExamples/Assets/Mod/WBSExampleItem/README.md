# 物品示例：联网计数器

这个示例适合学习物品的数据模型、场景与手持行为，以及只向持有者展示的私密信息。示例附带双人石头剪刀布玩法，用于体验物品交互。

选择“物品示例：联网计数器”玩法后，服务端会在双方出生点附近各生成一个计数器。拾取后拿到手中，执行“使用计数器”：服务端检查实际持有者，增加使用次数并同步展示。对局结束后清理本次生成的物品。

附带玩法需要两名真人参赛者，可以观战，不包含机器人策略。

## 从这些文件开始

| 文件 | 学习内容 |
| --- | --- |
| [WBSExampleItemMod.cs](Scripts/WBSExampleItemMod.cs) | 先登记附带玩法，再用 `RegisterItem` 登记 `Counter` |
| [CounterBehaviour.cs](Scripts/CounterBehaviour.cs) | `CounterData`、授权展示、拾取和手持操作、服务端权限检查 |
| [WBSExampleItemController.cs](Scripts/WBSExampleItemController.cs) | 对局开始时生成计数器，结束时清理；附带玩法逻辑 |
| [WBSExampleItemRulePanel.cs](Scripts/WBSExampleItemRulePanel.cs) | 附带玩法的规则页面与 Pad 操作 |
| `StaticData/Counter.asset` | 物品名称及 `Authorized` 展示变体 |
| `Art/Prefabs/Counter.prefab` | 联网、物理组件及作者行为 |
| `Art/Materials/CounterPlacement.mat` | 场景放置预览材质 |
| `StaticData/Config.asset`、`PadApps.asset`、`data/WBG/WBSExampleItem/` | 附带玩法的地图、Pad 地址与中英文数据 |

模组 KeyName 为 `WBSExampleItem`，物品 ID 为 `Counter`；打包配置为 `Editor/Build.asset`，入口程序集为 `WBSExampleItemModEntry`。

## 公开数据与授权展示

完整 `CounterData` 保存在服务端。普通展示不携带私密数字；持有者通过 `ToAuthorizedPresentation` 得到 `Authorized` 变体，其格式参数包括使用次数和私密数字。展示文字在 `Counter.asset` 中配置。

`CmdUse` 根据发送者的真实连接、参赛状态和当前手持 UID 检查请求。客户端不能通过提交一个玩家 ID 或物品 UID，就获得使用权限。公开的 `SyncVar` 只保存使用次数，不保存秘密。

## 可以先做的修改

1. 在 `Counter.asset` 中修改中英文名称和授权展示文本，保持 `{0}`、`{1}` 与代码生成的参数一致。
2. 修改 `Counter.prefab` 的外观，保留联网组件和 `CounterBehaviour` 引用。
3. 给 `CounterData` 增加服务端数据，并明确哪些内容允许公开、哪些只给持有者看。
4. 在 `GetHandActionList` 中增加操作，在服务端分别检查操作权限。

`Counter` 当前登记为通用物品，没有填写 `GameplayUuid`。需要限制为本模组某个玩法的物品时，按 [物品登记接口](../../../../../Documentation/接口协议.md#物品) 配置归属。

本例调用了部分游戏内部接口，打包报告可能给出兼容性警告。采用这些调用时，游戏更新后需要检查实际运行。

## 打包并体验

按照 [制作与打包说明](../../../../../Documentation/使用说明.md) 操作，选择本例的 `Editor/Build.asset`。联网检查时分别体验拾取、手持使用和私密展示，并确认未持有物品的玩家不能使用它。

做自己的物品时，通过新建向导取得独立模组身份，再参考本例的实现；不要沿用示例 UUID。接口查询见 [模组 API 参考](../../../../../Documentation/接口协议.md)。
