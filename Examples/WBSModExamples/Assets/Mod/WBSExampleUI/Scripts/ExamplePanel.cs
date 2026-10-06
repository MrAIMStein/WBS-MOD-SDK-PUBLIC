
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WBS.Client.Logic.ModSupport;
using WBS.Client.Logic.UI.ModUi;

namespace WBS.Client.Mod.WBSExampleUI
{
    // 状态与事件属于一次打开；关面板、退房或结束游戏后不会留下订阅。
    public sealed class ExamplePanel : MonoBehaviour, IModUiPanel
    {
        private int count;
        private ModUiSession owner;
        private ModUiSession overlay;
        // 固定控件由 Prefab 保存引用；展示用覆盖层可以没有按钮。
        public TMP_Text StatusText;
        public Button ActionButton;
        public Button ViewButton;
        public Button OverlayButton;
        public Button CloseButton;

        public void OnModPanelOpened(ModUiSession session)
        {
            OnModPanelClosing();
            if (StatusText == null) throw new System.InvalidOperationException("请在 Prefab 中绑定 StatusText。");
            owner = session;
            count = 0;
            if (ActionButton != null) ActionButton.onClick.AddListener(OpenPopup);
            if (ViewButton != null) ViewButton.onClick.AddListener(OpenView);
            if (OverlayButton != null) OverlayButton.onClick.AddListener(ToggleOverlay);
            if (CloseButton != null) CloseButton.onClick.AddListener(Close);
            Refresh();
        }

        // 本体负责 UI 接管与会话；标题、按钮及布局由作者提供。
        private void Close() { owner?.Close(); }

        private void OpenView()
        {
            if (owner == null || owner.IsClosed) return;
            // 返回的会话交给主界面；主界面关闭后也会清理作者打开的子界面。
            owner.Own(ModApi.For(owner.Context).Ui.OpenView("Art/Prefabs/Hud.prefab"));
            count++;
            Refresh();
        }

        private void OpenPopup()
        {
            if (owner == null || owner.IsClosed) return;
            owner.Own(ModApi.For(owner.Context).Ui.OpenPopup("模组界面演示",
                "确认后打开独立页面并切换覆盖层。取消会保留主界面。", () =>
                {
                    if (owner == null || owner.IsClosed) return;
                    OpenView();
                    ToggleOverlay();
                }));
        }

        private void ToggleOverlay()
        {
            if (owner == null || owner.IsClosed) return;
            if (overlay != null && !overlay.IsClosed)
            {
                overlay.Close();
                overlay = null;
            }
            else
            {
                var canvas = GetComponentInParent<Canvas>();
                if (canvas == null) throw new System.InvalidOperationException("模组界面必须位于 Canvas 下。");
                // 不入栈、不带背景；HUD 和提示层的锚点、位置与交互区域都保存在作者 Prefab 中。
                overlay = ModApi.For(owner.Context).Ui.OpenPanel("Art/Prefabs/Hud.prefab", canvas.transform,
                    useStack: false, useBg: false);
                owner.Own(overlay);
            }
            Refresh();
        }

        private void Refresh()
        {
            if (StatusText != null) StatusText.text = "打开页面：" + count + " 次\n覆盖层：" +
                (overlay != null && !overlay.IsClosed ? "已打开" : "已关闭");
        }

        public void OnModPanelClosing()
        {
            if (ActionButton != null) ActionButton.onClick.RemoveListener(OpenPopup);
            if (ViewButton != null) ViewButton.onClick.RemoveListener(OpenView);
            if (OverlayButton != null) OverlayButton.onClick.RemoveListener(ToggleOverlay);
            if (CloseButton != null) CloseButton.onClick.RemoveListener(Close);
            overlay?.Close();
            overlay = null;
            owner = null;
        }
    }
}
