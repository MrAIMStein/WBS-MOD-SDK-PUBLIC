using WBS.Client.Common.Mod;
using WBS.Client.Logic.ModSupport;
namespace WBS.Client.Mod.WBSExampleUI
{
    public sealed class WBSExampleUIMod : ModBase
    {
        public override bool OnLoad()
        {
            var api = ModApi.For(Context);
            api.Ui.RegisterMenuPanel("Example", "界面示例", "Art/Prefabs/Panel.prefab");
            // 只登记管理页入口；其他界面由按钮或玩法代码主动打开。
            return true;
        }
    }
}
