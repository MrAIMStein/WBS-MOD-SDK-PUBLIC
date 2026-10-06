using System;
using System.Collections.Generic;
using System.Linq;
using Mirror;
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using UGL.Unity.Network.Mirror;
using WBS.Client.Common.Mod;
using WBS.Client.Common.WBG;
using WBS.Client.Logic.Gameplay;
using WBS.Client.Logic.Gameplay.Item;
using WBS.Client.Logic.Gameplay.Map;
using WBS.Client.Logic.Gameplay.Pad;

namespace WBS.Client.Mod.WBSExampleGameplay
{
    /// <summary>静态控件来自 Prefab；规则页面和 Pad 应用共用此组件。</summary>
    public sealed class WBSExampleGameplayRulePanel : MonoBehaviour, IPadApp
    {
        public TMP_Text StatusText;
        public Button RockButton, ScissorsButton, PaperButton;
        private bool bound;
        private void OnEnable() { Bind(); Refresh(); }
        private void OnDisable() { Unbind(); }
        private void OnDestroy() { Unbind(); }
        public void InitApp() { Bind(); Refresh(); }
        public void OnOpen() { Bind(); Refresh(); }
        public void OnClose() { Unbind(); }
        private void Update() => Refresh();
        private void Bind()
        {
            if (bound) return; bound = true;
            RockButton?.onClick.AddListener(ChooseRock); ScissorsButton?.onClick.AddListener(ChooseScissors); PaperButton?.onClick.AddListener(ChoosePaper);
        }
        private void Unbind()
        {
            if (!bound) return; bound = false;
            RockButton?.onClick.RemoveListener(ChooseRock); ScissorsButton?.onClick.RemoveListener(ChooseScissors); PaperButton?.onClick.RemoveListener(ChoosePaper);
        }
        private void ChooseRock() => WBSExampleGameplayController.Instance?.RequestChoose(0);
        private void ChooseScissors() => WBSExampleGameplayController.Instance?.RequestChoose(1);
        private void ChoosePaper() => WBSExampleGameplayController.Instance?.RequestChoose(2);
        private void Refresh()
        {
            var controller = WBSExampleGameplayController.Instance;
            if (StatusText != null) StatusText.text = controller == null ? "双人石头剪刀布：每回合选择一次，双方完成或超时后计分。\n需要两名真人参赛者；从游戏内 Pad 打开本页操作。" : controller.Status;
            bool canUse = bound && controller != null && controller.CanSubmit;
            if (RockButton != null) RockButton.interactable = canUse;
            if (ScissorsButton != null) ScissorsButton.interactable = canUse;
            if (PaperButton != null) PaperButton.interactable = canUse;
        }
    }
}
