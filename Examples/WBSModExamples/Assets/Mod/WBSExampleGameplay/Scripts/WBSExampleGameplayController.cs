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
    [Serializable]
    public sealed class WBSExampleGameplaySettings : WBGSettings, IRequiredPlayingPlayerCountSettings, IPlayerAssignmentSettings
    {
        [WBGSettingsUIEntrySliderInt(defaultValue = 5, minValue = 1, maxValue = 20, labelKey = "Rounds", order = 0)]
        public int Rounds = 5;
        [WBGSettingsUIEntrySliderInt(defaultValue = 15, minValue = 5, maxValue = 60, labelKey = "Seconds", order = 1)]
        public int Seconds = 15;
        public int RequiredPlayingPlayerCount => 2;
        public PlayerNumberAssignmentMode PlayerNumberMode => PlayerNumberAssignmentMode.Seat;
        public PlayerInitialPositionMode PlayerInitialPositionMode => PlayerInitialPositionMode.Number;
    }

    /// <summary>选择只留在服务端，双方完成或超时后才公开结果；客户端不声明自己的身份或得分。</summary>
    public sealed class WBSExampleGameplayController : WBGControllerBase, IWBGGameplayActionPolicy, IWBGReviewPhasePolicy
    {
        public const string GameplayUuid = "d7b230da-1c9c-4f62-9cd7-e27f8d079204";
        public static WBSExampleGameplayController Instance { get; private set; }
        [SyncVar] private uint matchId;
        [SyncVar] private UserId seatOne;
        [SyncVar] private UserId seatTwo;
        [SyncVar] private int round;
        [SyncVar] private int roundLimit;
        [SyncVar] private int scoreOne;
        [SyncVar] private int scoreTwo;
        [SyncVar] private byte phase;
        [SyncVar] private double deadline;
        [SyncVar] private string summary;
        private int choiceOne = -1, choiceTwo = -1;
        private int roundSeconds;
        private double startedAt;
        private int acceptedRound;
        private UserId receiptOwner;
        private bool initialized, listening;
        private static bool SpawnCounterExample => false;
        private readonly List<int> exampleItemUids = new();
        private string localFeedback = string.Empty;
        public bool IsInReview => phase == 3;
        public bool AllowGameplayActions => initialized && phase == 1 && NetworkTime.time < deadline;
        public int LocalSeat => NetworkUtils.GetUserIdSelf() == seatOne ? 1 : NetworkUtils.GetUserIdSelf() == seatTwo ? 2 : 0;
        public bool HasSubmitted => receiptOwner == NetworkUtils.GetUserIdSelf() && acceptedRound == round;
        public bool CanSubmit => LocalSeat != 0 && AllowGameplayActions && !HasSubmitted;
        public string Status => "双人石头剪刀布：石头胜剪刀，剪刀胜布，布胜石头。\n" +
            "第 " + round + " / " + roundLimit + " 回合　1号 " + scoreOne + " : " + scoreTwo + " 2号\n" +
            (phase == 1 ? "剩余 " + Math.Max(0, deadline - NetworkTime.time).ToString("F0") + " 秒。" : "") +
            (LocalSeat == 0 ? "观战模式" : "你是 " + LocalSeat + " 号；" + (HasSubmitted ? "本回合已确认。" : "请选择一次并等待结算。")) +
            "\n" + (summary ?? string.Empty) + "\n" + localFeedback;

        private void Awake() => Instance = this;
        public override void Load() { acceptedRound = 0; receiptOwner = UserId.Nil; localFeedback = string.Empty; }
        public override void Init()
        {
            if (initialized) return;
            initialized = true;
            if (!NetworkServer.active) return;
            var players = GameMemberManager.Instance.MemberList.Where(member => member != null && !member.IsSpectating)
                .OrderBy(member => member.Idx).ToArray();
            if (players.Length != 2 || players.Any(member => member.IsBot))
                throw new InvalidOperationException("此示例需要两名真人参赛者；机器人适配不在示例范围。");
            seatOne = players[0].Id; seatTwo = players[1].Id;
            var settings = (WBSExampleGameplaySettings)GameExecutor.Instance.Select.Settings;
            roundLimit = Mathf.Clamp(settings.Rounds, 1, 20); roundSeconds = Mathf.Clamp(settings.Seconds, 5, 60);
            matchId = GameExecutor.Instance.StartupId; scoreOne = 0; scoreTwo = 0; round = 0; startedAt = NetworkTime.time;
            if (GameController.TryGetInstance(out var game)) game.GameTime = 0;
            SpawnCountersServer();
            CustomNetworkManager.ServerDisconnecting += OnDisconnecting; listening = true;
            BeginRoundServer();
        }
        public override void Unload()
        {
            if (listening) CustomNetworkManager.ServerDisconnecting -= OnDisconnecting;
            ClearCountersServer();
            listening = false; initialized = false; acceptedRound = 0; receiptOwner = UserId.Nil;
            localFeedback = string.Empty;
        }
        public override void OnStopServer() { Unload(); base.OnStopServer(); }
        public override void OnStopClient() { Unload(); base.OnStopClient(); }
        private void OnDestroy() { Unload(); if (Instance == this) Instance = null; }

        [Server] private void SpawnCountersServer()
        {
            if (!SpawnCounterExample) return;
            var info = ModManager.Instance.SelectModInfoByKeyName("WBSExampleGameplay");
            if (info == null) throw new InvalidOperationException("计数器示例模组上下文不存在。");
            string itemId = ModStaticDataManager.GetItemIdCommon(ModStaticDataManager.GetItemIdMod("Counter", info.UUID));
            var items = ItemManager.Instance;
            try
            {
                foreach (UserId owner in new[] { seatOne, seatTwo })
                {
                    Transform spawn = MapManager.Instance.GetInitialPosition(owner);
                    if (spawn == null) throw new InvalidOperationException("计数器示例缺少玩家出生位置。");
                    int uid = items.NewItemServer(itemId);
                    if (uid <= 0) throw new InvalidOperationException("计数器静态定义未登记。");
                    exampleItemUids.Add(uid);
                    var position = new ItemModelInitParams { position = spawn.position + spawn.forward + Vector3.up * .5f, rotation = Quaternion.identity };
                    if (!items.TryPlaceExistingItemInSceneServer(uid, position, out ItemTransferResult result))
                        throw new InvalidOperationException("动态生成计数器失败：" + result);
                }
            }
            catch { ClearCountersServer(); throw; }
        }
        private void ClearCountersServer()
        {
            // 只清理此控制器创建的 UID；拾取进背包或手中后仍属于本次示例对局。
            if (NetworkServer.active && ItemManager.TryGetInstance(out var items))
                foreach (int uid in exampleItemUids) if (items.CheckItemExist(uid)) items.TryConsumeItemServer(uid);
            exampleItemUids.Clear();
        }

        private void Update()
        {
            if (!NetworkServer.active || !initialized) return;
            if (GameExecutor.TryGetInstance(out var executor) && (!executor.IsGaming || executor.StartupId != matchId)) return;
            if (phase == 1 && (choiceOne >= 0 && choiceTwo >= 0 || NetworkTime.time >= deadline)) ResolveRoundServer();
            else if (phase == 2 && NetworkTime.time >= deadline)
            {
                if (round >= roundLimit) FinishServer(scoreOne == scoreTwo ? "全局平局" : scoreOne > scoreTwo ? "1号获胜" : "2号获胜");
                else BeginRoundServer();
            }
            if (GameController.TryGetInstance(out var game))
            { game.Countdown = phase == 1 ? (float)Math.Max(0, deadline - NetworkTime.time) : 0; game.StageNameKeyFull = "WBGTable.SDK_Round";
                if (phase != 3) game.GameTime = (float)Math.Max(0, NetworkTime.time - startedAt); }
        }
        [Server] private void BeginRoundServer()
        { round++; choiceOne = -1; choiceTwo = -1; phase = 1; deadline = NetworkTime.time + roundSeconds; summary = "等待双方提交；超时未提交的一方判负。"; }
        [Server] private void ResolveRoundServer()
        {
            int winner = choiceOne < 0 && choiceTwo < 0 || choiceOne == choiceTwo ? 0 :
                choiceOne < 0 ? 2 : choiceTwo < 0 ? 1 : (choiceOne + 1) % 3 == choiceTwo ? 1 : 2;
            if (winner == 1) scoreOne++; else if (winner == 2) scoreTwo++;
            summary = "回合 " + round + "：1号 " + ChoiceName(choiceOne) + "；2号 " + ChoiceName(choiceTwo) +
                "；" + (winner == 0 ? "平局" : winner + "号得1分");
            phase = 2; deadline = NetworkTime.time + 2;
            RoundCompletedRpc(matchId, round, summary);
        }
        private static string ChoiceName(int choice) => choice == 0 ? "石头" : choice == 1 ? "剪刀" : choice == 2 ? "布" : "未提交";

        public void RequestChoose(int choice)
        {
            if (!NetworkClient.active || !CanSubmit) return;
            if (NetworkServer.active)
                ApplyReceipt(matchId, round, NetworkUtils.GetUserIdSelf(), SubmitServer(NetworkServer.localConnection, matchId, round, choice));
            else SubmitCommand(matchId, round, choice);
        }
        [Command(requiresAuthority = false)]
        private void SubmitCommand(uint expectedMatch, int expectedRound, int choice, NetworkConnectionToClient sender = null)
        {
            if (sender == null) return;
            string result = SubmitServer(sender, expectedMatch, expectedRound, choice);
            UserId owner = TryResolveSender(sender, out var id) ? id : UserId.Nil;
            SubmissionReceiptRpc(sender, expectedMatch, expectedRound, owner, result);
        }
        private string SubmitServer(NetworkConnectionToClient sender, uint expectedMatch, int expectedRound, int choice)
        {
            if (!NetworkServer.active || !TryResolveSender(sender, out UserId owner) || owner != seatOne && owner != seatTwo) return "身份无效或正在观战";
            if (expectedMatch != matchId || expectedRound != round || !AllowGameplayActions ||
                !GameExecutor.TryGetInstance(out var executor) || !executor.IsGaming || executor.StartupId != matchId) return "回合已变更或已截止";
            if (choice < 0 || choice > 2) return "选择无效";
            if (owner == seatOne) { if (choiceOne >= 0) return "本回合已提交"; choiceOne = choice; }
            else { if (choiceTwo >= 0) return "本回合已提交"; choiceTwo = choice; }
            return string.Empty;
        }
        private static bool TryResolveSender(NetworkConnectionToClient sender, out UserId owner)
        {
            owner = UserId.Nil;
            if (sender == null || !sender.isAuthenticated || sender.identity == null || sender.identity.connectionToClient != sender ||
                !sender.identity.TryGetComponent(out MirrorNetworkIdentifier identity) || identity.Owner == UserId.Nil) return false;
            owner = identity.Owner;
            return ReferenceEquals(NetworkPlayerHandlerUtils.GetTargetConnByUserId(owner), sender);
        }
        [TargetRpc] private void SubmissionReceiptRpc(NetworkConnectionToClient target, uint expectedMatch, int expectedRound, UserId owner, string error)
            => ApplyReceipt(expectedMatch, expectedRound, owner, error);
        private void ApplyReceipt(uint expectedMatch, int expectedRound, UserId owner, string error)
        {
            if (expectedMatch != matchId || expectedRound != round || owner != NetworkUtils.GetUserIdSelf()) return;
            localFeedback = string.IsNullOrEmpty(error) ? "提交已确认；选择将在结算时公开。" : error;
            if (string.IsNullOrEmpty(error)) { acceptedRound = expectedRound; receiptOwner = owner; }
        }
        [ClientRpc] private void RoundCompletedRpc(uint expectedMatch, int completedRound, string message)
        { if (expectedMatch == matchId && completedRound == round) localFeedback = message; }

        private void OnDisconnecting(NetworkConnectionToClient sender)
        {
            if (!NetworkServer.active || phase == 3 || !TryResolveSender(sender, out UserId owner)) return;
            if (owner == seatOne || owner == seatTwo) FinishServer((owner == seatOne ? "2号" : "1号") + "获胜：对手断开连接。");
        }
        [Server] private void FinishServer(string result)
        {
            if (phase == 3) return;
            phase = 3; deadline = 0; summary = "对局结束：" + result + "。可通过游戏菜单返回。";
            RoundCompletedRpc(matchId, round, summary);
            if (MatchManager.TryGetInstance(out var matches)) matches.WBGEnd();
        }
    }

}
