
namespace WBS.Client.Mod.WBSExampleItem
{
    [System.Serializable]
    public sealed class CounterData : WBS.Client.Logic.Gameplay.Item.ItemDataModel
    {
        public int Uses;
        public int SecretNumber = 7;
        // 公开副本沿用基础类，私密值只进入持有者的授权展示。
        public override WBS.Client.Logic.Gameplay.Item.ItemPresentation ToAuthorizedPresentation(int uid)
        {
            var result = ToPresentation(uid);
            result.PresentationVariantId = "Authorized";
            result.NameFormatArguments = new[] { Uses.ToString(), SecretNumber.ToString() };
            return result;
        }
    }
    public sealed class CounterBehaviour : WBS.Client.Logic.Gameplay.Item.ItemBehaviour
    {
        [Mirror.SyncVar] private int uses;
        [Mirror.Server]
        public override void Init(int uid)
        {
            base.Init(uid);
            if (WBS.Client.Logic.Gameplay.Item.ItemManager.Instance.TryGetItemDataByUid(uid, out var data) && data is CounterData counter)
                uses = counter.Uses;
        }
        public override System.Collections.Generic.List<WBS.Client.Logic.Gameplay.Item.ItemAction> GetSceneActionList() =>
            new() { new() { label = "拾取计数器", action = () => WBS.Client.Logic.Gameplay.Bag.BagItemManager.Instance.TryPickItem(Uid) } };
        public override System.Collections.Generic.List<WBS.Client.Logic.Gameplay.Item.ItemAction> GetHandActionList() =>
            new() { new() { label = "使用计数器（" + uses + "）", action = () => CmdUse() } };
        [Mirror.Command(requiresAuthority = false)]
        private void CmdUse(Mirror.NetworkConnectionToClient sender = null)
        {
            if (sender == null || !sender.isAuthenticated || sender.identity == null || sender.identity.connectionToClient != sender) return;
            var identity = sender.identity.GetComponent<UGL.Unity.Network.Mirror.MirrorNetworkIdentifier>();
            if (identity == null || identity.Owner == UGL.Unity.Network.Mirror.UserId.Nil ||
                !ReferenceEquals(UGL.Unity.Network.Mirror.NetworkPlayerHandlerUtils.GetTargetConnByUserId(identity.Owner), sender) ||
                !WBS.Client.Logic.Gameplay.GameExecutor.TryGetInstance(out var executor) || !executor.IsGaming ||
                !WBS.Client.Logic.Gameplay.PlayerParticipationStateManager.Instance.CanPerformGameplayActions(identity.Owner)) return;
            if (identity == null || !WBS.Client.Logic.Gameplay.Item.HandItemManager.Instance.TryGetHandItemUidServer(identity.Owner, out int held) || held != Uid) return;
            var items = WBS.Client.Logic.Gameplay.Item.ItemManager.Instance;
            if (!items.TryGetItemDataByUid(Uid, out var data) || data is not CounterData counter) return;
            counter.Uses++; uses = counter.Uses; items.TrySyncItemDataServer(Uid); RpcUsed(uses);
        }
        [Mirror.ClientRpc] private void RpcUsed(int value) { UnityEngine.Debug.Log("计数器已使用：" + value); }
    }
}
