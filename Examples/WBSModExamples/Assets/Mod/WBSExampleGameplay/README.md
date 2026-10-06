# 玩法示例：石头剪刀布

这个示例适合学习如何把玩法设置、地图、规则页和联网控制器连接起来。它需要两名真人参赛者，允许其他成员观战；不包含机器人策略。

默认进行 5 回合，每回合限时 15 秒。玩家通过游戏内 Pad 提交石头、剪刀或布；双方完成出手或计时结束后，服务端公布结果并计分。可在玩法设置中调整回合数和时限。

## 从这些文件开始

| 文件 | 学习内容 |
| --- | --- |
| [WBSExampleGameplayMod.cs](Scripts/WBSExampleGameplayMod.cs) | 用 `RegisterGameplay` 连接玩法身份、设置、控制器和资源地址 |
| [WBSExampleGameplayController.cs](Scripts/WBSExampleGameplayController.cs) | 设置字段、回合状态、出手请求、服务端判定与计分、卸载清理 |
| [WBSExampleGameplayRulePanel.cs](Scripts/WBSExampleGameplayRulePanel.cs) | 按钮绑定、玩家状态展示；规则页面与 Pad 共用组件 |
| `Art/Prefabs/Controller.prefab` | 联网玩法控制器，包含 `NetworkIdentity` |
| `Art/Prefabs/Map1.prefab` | 地图及两个出生点，地图 ID 为 `1` |
| `Art/Prefabs/RulePanel.prefab` | 状态文字与石头、剪刀、布按钮 |
| `StaticData/Config.asset`、`PadApps.asset` | 地图清单与 Pad 页面地址 |
| `data/WBG/WBSExampleGameplay/` | 玩法身份、中英文内容与设置说明 |

模组 KeyName 为 `WBSExampleGameplay`，打包配置为 `Editor/Build.asset`，入口程序集为 `WBSExampleGameplayModEntry`。

## 可以先做的修改

1. 在控制器文件的 `WBSExampleGameplaySettings` 中调整回合和时限的默认值、范围。
2. 在 `RulePanel.prefab` 中修改布局、按钮和文本，保留对应组件字段引用。
3. 添加设置或可见文本时，同步修改数据目录中的中英文表。
4. 修改地图时保留两个出生点；新增地图后同时更新 `Config.asset` 与入口中的 `Maps`。

出手与比分的判定放在服务端。客户端只发操作请求，服务端根据真实连接检查参赛身份、回合和截止时间。双方出手在结算前不公开；不要把私密选择改成广播的 `SyncVar`。

本例调用了部分游戏内部接口，打包报告可能给出兼容性警告。采用这些调用时，游戏更新后需要检查实际运行。

## 打包并体验

按照 [制作与打包说明](../../../../../Documentation/使用说明.md) 操作，选择本例的 `Editor/Build.asset`。进入游戏后选择“玩法示例：石头剪刀布”，安排两名真人参赛，从 Pad 打开“计分回合”页面操作。两名玩家使用同一份模组包。

做自己的玩法时，通过新建向导取得独立身份，再参考本例的实现；不要沿用示例 UUID。接口查询见 [模组 API 参考](../../../../../Documentation/接口协议.md)。
