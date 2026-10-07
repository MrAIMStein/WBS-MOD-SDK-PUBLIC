using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Mirror;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TMPro;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using WBS.Client.Common.Mod;
using WBS.Client.Common.WBG;
using WBS.Client.Editor.CharacterAuthoring;
using WBS.Client.Logic.Gameplay.Item;
using WBS.Client.Logic.Gameplay.Map;
using WBS.Client.Logic.Gameplay.Pad;
using Object = UnityEngine.Object;

namespace WBS.Client.Editor.ModSDK
{
    /// <summary>源码先编译、资产随后通过 Unity API 绑定。失败请求保留诊断，不重复覆盖作者内容。</summary>
    [InitializeOnLoad]
    public static class ModAuthoringScaffold
    {
        private const string PendingRoot = "Library/ModSDK/NewMods";
        private static readonly UTF8Encoding Utf8 = new(false);
        private static double nextCheck;
        [Serializable] private sealed class Pending { public string KeyName; public string GameplayUuid; public ModAuthoringKind Kind; }
        static ModAuthoringScaffold() { EditorApplication.update += ProcessPending; }

        public static ModBuildProfile Create(ModAuthoringKind kind, string keyName, string name, string author, string uuid = null, string gameplayUuid = null, bool refresh = true)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("请等待编辑器完成编译，并在编辑模式创建模组。");
            if (!Regex.IsMatch(keyName ?? string.Empty, "^[A-Za-z][A-Za-z0-9_]{0,63}$") || string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("目录标识须以英文字母开头，只包含字母、数字和下划线；模组名称不能为空。");
            if (!Enum.IsDefined(typeof(ModAuthoringKind), kind)) throw new ArgumentException("未知模组类型。");
            uuid ??= Guid.NewGuid().ToString("D");
            gameplayUuid ??= Guid.NewGuid().ToString("D");
            if (!Guid.TryParseExact(uuid, "D", out _) || !Guid.TryParseExact(gameplayUuid, "D", out _)) throw new ArgumentException("UUID 必须为标准 GUID。");
            string root = "Assets/Mod/" + keyName;
            if (Directory.Exists(root) || File.Exists(root + ".meta")) throw new IOException("作者目录已存在，未覆盖：" + root);
            EnsureFolder(root + "/Editor");
            EnsureFolder(root + "/Art/Prefabs");
            Directory.CreateDirectory(root + "/data");
            var profile = ScriptableObject.CreateInstance<ModBuildProfile>();
            profile.Info = new ModInfo { UUID = uuid, KeyName = keyName, Name = name.Trim(), Author = author,
                Version = "1.0.0", RequiredModApiVersion = ModSDKEnvironment.InstalledModApiVersion, Dependencies = new List<string>() };
            profile.Mode = kind == ModAuthoringKind.Model ? ModBuildMode.Model : ModBuildMode.Custom;
            profile.AuthoringKind = kind;
            profile.SourceRoot = root;
            profile.DataSourceDirectory = root + "/data";
            AssetDatabase.CreateAsset(profile, root + "/Editor/Build.asset");
            if (kind != ModAuthoringKind.Model)
            {
                EnsureFolder(root + "/Scripts");
                WriteAuthorAssembly(root + "/Scripts/" + keyName + "ModEntry.asmdef", keyName);
                File.WriteAllText(root + "/Scripts/" + keyName + "Mod.cs", EntrySource(kind, keyName, gameplayUuid), Utf8);
                if (kind == ModAuthoringKind.Gameplay || kind == ModAuthoringKind.Item)
                    foreach (var source in ModAuthoringGameplayTemplate.CreateSources(keyName, gameplayUuid, kind == ModAuthoringKind.Item))
                        File.WriteAllText(root + "/Scripts/" + source.Key, source.Value, Utf8);
                if (kind == ModAuthoringKind.Item) File.WriteAllText(root + "/Scripts/CounterBehaviour.cs", ItemSource.Replace("__KEY__", keyName), Utf8);
                if (kind == ModAuthoringKind.UI) File.WriteAllText(root + "/Scripts/ExamplePanel.cs", UiSource.Replace("__KEY__", keyName), Utf8);
            }
            File.WriteAllText(root + "/README.md", "# " + name + "\n\nUUID：`" + uuid + "`。SDK：`" + ModSDKEnvironment.InstalledVersion +
                "`，模组协议：`" + ModSDKEnvironment.InstalledModApiVersion + "`。\n\n" +
                "公共资源引用 SDK；作者模型、材质、Prefab 和 C# 留在本目录。编译完成后会生成示例资源。" +
                "使用 WBS/模组/打包工具选择 Editor/Build.asset；人物模型先使用人物制作工具替换模型并生成成品。" +
                "调试时选择匹配 SDK 的 WBS.exe，点击打包并启动调试，以 --dev 使用 WBS_Dev 数据，查看游戏日志；支持脚本调试的构建也可在 IDE 中附加 WBS 进程。" +
                "修改代码后重新打包并重启宿主。正式发布关闭 DebugBuild。\n", Utf8);
            Directory.CreateDirectory(PendingRoot);
            File.WriteAllText(PendingRoot + "/" + keyName + ".json", JsonConvert.SerializeObject(new Pending { KeyName = keyName, Kind = kind, GameplayUuid = gameplayUuid }), Utf8);
            AssetDatabase.SaveAssetIfDirty(profile);
            if (refresh) AssetDatabase.Refresh();
            return profile;
        }

        public static void WriteAuthorAssembly(string path, string keyName)
        {
            var packages = new[] { "Unity.TextMeshPro", "UnityEngine.UI" };
            bool installed = !string.IsNullOrEmpty(ModSDKEnvironment.PackageRoot);
            string[] refs = installed ? packages : CompilationPipeline.GetAssemblies(AssembliesType.Player)
                .Where(assembly => assembly.sourceFiles.Length > 0 && (assembly.name.StartsWith("WBS.Client.", StringComparison.Ordinal) ||
                    assembly.name.StartsWith("UGL", StringComparison.Ordinal) || assembly.name == "Mirror" || assembly.name == "Mirror.Components"))
                .Select(assembly => assembly.name).Concat(packages).Distinct().OrderBy(value => value, StringComparer.Ordinal).ToArray();
            var json = new JObject { ["name"] = keyName + "ModEntry", ["rootNamespace"] = "WBS.Client.Mod." + keyName,
                ["references"] = new JArray(refs), ["autoReferenced"] = true, ["overrideReferences"] = installed,
                ["defineConstraints"] = new JArray(installed ? Array.Empty<string>() : new[] { "!WBS_HOST_BUILD" }),
                ["precompiledReferences"] = new JArray(installed ? ModSDKEnvironment.ReadManifest().runtimeAssemblyNames
                    .Where(value => File.Exists(ModSDKEnvironment.GetPhysicalPath(ModSDKEnvironment.PackageRoot + "/Runtime/Assemblies/" + value + ".dll")))
                    .Select(value => value + ".dll") : Array.Empty<string>()),
                ["noEngineReferences"] = false };
            File.WriteAllText(path, json.ToString() + "\n", Utf8);
        }

        private static void ProcessPending()
        {
            if (EditorApplication.timeSinceStartup < nextCheck || EditorApplication.isCompiling || EditorApplication.isUpdating ||
                EditorApplication.isPlayingOrWillChangePlaymode || EditorUtility.scriptCompilationFailed || !Directory.Exists(PendingRoot)) return;
            nextCheck = EditorApplication.timeSinceStartup + 2;
            foreach (string path in Directory.GetFiles(PendingRoot, "*.json").Where(value => !value.EndsWith(".failed.json", StringComparison.Ordinal)))
            {
                try
                {
                    var pending = JsonConvert.DeserializeObject<Pending>(File.ReadAllText(path));
                    if (pending == null || !Regex.IsMatch(pending.KeyName ?? "", "^[A-Za-z][A-Za-z0-9_]{0,63}$")) throw new InvalidDataException("制作请求标识无效。");
                    if (pending.Kind != ModAuthoringKind.Model && FindType(pending.KeyName, pending.KeyName + "Mod") == null) continue;
                    Complete(pending);
                    File.Delete(path);
                }
                catch (Exception error)
                {
                    string failedPath = Path.Combine(PendingRoot, Path.GetFileNameWithoutExtension(path) + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".failed.json");
                    File.WriteAllText(Path.ChangeExtension(failedPath, ".error.txt"), error.ToString(), Utf8);
                    File.Move(path, failedPath);
                    Debug.LogException(error);
                }
            }
        }

        private static void Complete(Pending pending)
        {
            string root = "Assets/Mod/" + pending.KeyName;
            var profile = AssetDatabase.LoadAssetAtPath<ModBuildProfile>(root + "/Editor/Build.asset") ?? throw new InvalidDataException("制作配置丢失。");
            if (pending.Kind == ModAuthoringKind.Model) CreateModel(profile);
            else if (pending.Kind == ModAuthoringKind.Gameplay) CreateGameplay(profile, pending.GameplayUuid);
            else if (pending.Kind == ModAuthoringKind.Item) { CreateItem(profile); CreateGameplay(profile, pending.GameplayUuid); }
            else CreateUi(profile);
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
            Debug.Log("模组向导已完成资源绑定：" + root);
        }

        private static void CreateModel(ModBuildProfile profile)
        {
            EnsureFolder(profile.SourceRoot + "/AuthoringSource");
            var template = AssetDatabase.LoadAssetAtPath<GameObject>(ModSDKEnvironment.ResolveAssetPath(ModSDKEnvironment.DefaultTemplatePath));
            Transform visual = template != null ? template.transform.Find("Model/Model/Model") : null;
            var animator = template != null ? template.transform.Find("Model/Model")?.GetComponent<Animator>() : null;
            if (visual == null || animator?.avatar == null) throw new InvalidDataException("SDK 模板的美术源或 Avatar 缺失。");
            var source = Object.Instantiate(visual.gameObject);
            try
            {
                source.name = "ExampleSource";
                foreach (Component component in source.GetComponentsInChildren<Component>(true))
                    if (component != null && component is not Transform && component is not Renderer && component is not MeshFilter)
                        Object.DestroyImmediate(component);
                source.AddComponent<Animator>().avatar = animator.avatar;
                var sourcePrefab = SavePrefab(source, profile.SourceRoot + "/AuthoringSource/ExampleSource.prefab");
                var recipe = ScriptableObject.CreateInstance<CharacterAuthoringRecipe>();
                recipe.ModelId = "Example"; recipe.DisplayName = profile.Info.Name; recipe.EnglishDisplayName = profile.Info.KeyName;
                recipe.Destination = CharacterAuthoringDestination.Mod; recipe.ModKeyName = profile.Info.KeyName; recipe.ModUuid = profile.Info.UUID;
                recipe.SourcePrefab = sourcePrefab; recipe.SourceCredit = "WBS SDK 公共人物模板";
                recipe.LicenseReference = "公共演示；替换外部模型时重新填写来源和许可";
                recipe.PermissionsConfirmed = true; recipe.CommercialUse = CharacterLicenseDeclaration.Allowed; recipe.SourceRedistribution = CharacterLicenseDeclaration.Allowed;
                AssetDatabase.CreateAsset(recipe, profile.SourceRoot + "/Editor/Recipe.asset");
                profile.CharacterRecipes.Add(recipe);
                var result = CharacterAuthoringBuilder.Generate(recipe);
                if (!result.Success) throw new InvalidDataException(result.ToString());
            }
            finally { Object.DestroyImmediate(source); }
        }

        private static void CreateGameplay(ModBuildProfile profile, string uuid)
        {
            string root = profile.SourceRoot;
            var controller = new GameObject(profile.Info.KeyName + "Controller", typeof(NetworkIdentity));
            try { controller.AddComponent(RequireType(profile.Info.KeyName, profile.Info.KeyName + "Controller"));
                profile.ResourceEntries.Add(SavePrefab(controller, root + "/Art/Prefabs/Controller.prefab")); }
            finally { Object.DestroyImmediate(controller); }
            var map = new GameObject("Map1", typeof(MapHandler), typeof(WBGMapBase));
            try
            {
                var handler = map.GetComponent<MapHandler>();
                handler.PositionCharacterInitList = new List<Transform>();
                for (int i = 0; i < 2; i++) { var spawn = new GameObject("Spawn" + (i + 1)); spawn.transform.SetParent(map.transform); spawn.transform.localPosition = new Vector3(i == 0 ? -2 : 2, 1, 0); handler.PositionCharacterInitList.Add(spawn.transform); }
                handler.PositionCharacterInitDefault = handler.PositionCharacterInitList[0];
                var ground = GameObject.CreatePrimitive(PrimitiveType.Cube); ground.name = "Ground"; ground.transform.SetParent(map.transform); ground.transform.localScale = new Vector3(20, .2f, 20);
                ground.GetComponent<Renderer>().sharedMaterial = CreateToon(profile, "Ground", new Color(.6f, .65f, .7f), false);
                var light = new GameObject("DirectionalLight", typeof(Light)); light.transform.SetParent(map.transform); light.GetComponent<Light>().type = LightType.Directional; light.transform.localRotation = Quaternion.Euler(50, -30, 0);
                profile.ResourceEntries.Add(SavePrefab(map, root + "/Art/Prefabs/Map1.prefab"));
            }
            finally { Object.DestroyImmediate(map); }
            var rule = CreatePanel("RulePanel", "双人计分示例：在 Pad 中确认得分，限时结束后查看结果。", "确认得分");
            try
            {
                var script = rule.AddComponent(RequireType(profile.Info.KeyName, profile.Info.KeyName + "RulePanel"));
                var status = rule.transform.Find("Status").GetComponent<TMP_Text>();
                SetRequiredField(script, "StatusText", status);
                var rock = rule.transform.Find("ActionButton").GetComponent<UnityEngine.UI.Button>();
                rock.name = "RockButton";
                rock.GetComponentInChildren<TMP_Text>().text = "石头";
                var scissors = Object.Instantiate(rock, rule.transform, false); scissors.name = "ScissorsButton"; scissors.GetComponentInChildren<TMP_Text>().text = "剪刀";
                var paper = Object.Instantiate(rock, rule.transform, false); paper.name = "PaperButton"; paper.GetComponentInChildren<TMP_Text>().text = "布";
                var buttons = new[] { rock, scissors, paper };
                for (int i = 0; i < buttons.Length; i++) { var rect = (RectTransform)buttons[i].transform; rect.anchorMin = new Vector2(.05f + i * .32f, .06f); rect.anchorMax = new Vector2(.31f + i * .32f, .25f); rect.offsetMin = rect.offsetMax = Vector2.zero; }
                SetRequiredField(script, "RockButton", rock);
                SetRequiredField(script, "ScissorsButton", scissors);
                SetRequiredField(script, "PaperButton", paper);
                profile.ResourceEntries.Add(SavePrefab(rule, root + "/Art/Prefabs/RulePanel.prefab"));
            }
            finally { Object.DestroyImmediate(rule); }
            EnsureFolder(root + "/StaticData");
            var config = ScriptableObject.CreateInstance<WBGConfig>(); config.wbgItemIdList = new List<string>(); config.wbgMapIdList = new List<string> { "1" };
            AssetDatabase.CreateAsset(config, root + "/StaticData/Config.asset"); profile.ResourceEntries.Add(config);
            var apps = ScriptableObject.CreateInstance<PadAppInfoListSO>(); apps.infoList = new List<PadAppInfo> { new() { Name = "计分回合", PnlPath = root + "/Art/Prefabs/RulePanel.prefab" } };
            AssetDatabase.CreateAsset(apps, root + "/StaticData/PadApps.asset"); profile.ResourceEntries.Add(apps);
            Directory.CreateDirectory(root + "/data/WBG/" + profile.Info.KeyName);
            File.WriteAllText(root + "/data/WBG/" + profile.Info.KeyName + "/wbginfo.json", JsonConvert.SerializeObject(new WBGInfo { UUID = uuid, KeyName = profile.Info.KeyName, Version = "1.0.0" }), Utf8);
            foreach (string language in new[] { "ChineseSimplified", "English" })
            {
                bool chinese = language == "ChineseSimplified";
                string directory = root + "/data/WBG/" + profile.Info.KeyName + "/";
                File.WriteAllText(directory + profile.Info.KeyName + "WBGTable_" + language + ".csv", "Key,Text\nSDK_Round," + (chinese ? "限时回合" : "Timed round") + "\n", Utf8);
                File.WriteAllText(directory + profile.Info.KeyName + "WBGMetaTable_" + language + ".csv", "Key,Text\nCode_Meta_Name," +
                    (chinese ? profile.Info.Name.Replace(',', '，').Replace('\n', ' ') : profile.Info.KeyName) + "\nCode_Meta_Description," +
                    (chinese ? "双人限时示例；通过Pad出手并查看公开比分" : "Two player timed example with public scores") +
                    "\nCode_Settings_Rounds," + (chinese ? "回合数" : "Rounds") + "\nCode_Settings_Seconds," + (chinese ? "每回合秒数" : "Seconds per round") + "\n", Utf8);
            }
        }

        private static void CreateItem(ModBuildProfile profile)
        {
            string root = profile.SourceRoot;
            EnsureFolder(root + "/StaticData");
            var info = ScriptableObject.CreateInstance<ItemInfoSO>();
            info.info = new ItemInfo { ItemId = "Counter", ItemShortId = "Counter" };
            info.info.DisplayName = WBS.Client.Logic.Gameplay.Localization.LocalizedContentText.Create("示例计数器", "Example Counter");
            info.info.PresentationVariants.Add(new ItemPresentationVariant { Key = "Authorized",
                DisplayName = WBS.Client.Logic.Gameplay.Localization.LocalizedContentText.Create("计数器：已使用 {0} 次，私密数字 {1}", "Counter: {0} uses, private number {1}"),
                Description = WBS.Client.Logic.Gameplay.Localization.LocalizedContentText.Create("只有物品持有者能看到私密数字 {1}。", "Only the owner sees private number {1}.") });
            AssetDatabase.CreateAsset(info, root + "/StaticData/Counter.asset"); profile.ResourceEntries.Add(info);
            var item = GameObject.CreatePrimitive(PrimitiveType.Cube); item.name = "Counter"; item.transform.localScale = Vector3.one * .25f;
            try
            {
                item.AddComponent<NetworkIdentity>(); item.AddComponent<Rigidbody>(); item.AddComponent<NetworkTransformReliable>();
                var physics = item.AddComponent<ItemPhysics>(); physics.Body = item.GetComponent<Rigidbody>(); physics.PhysicalCollider = item.GetComponent<BoxCollider>();
                item.GetComponent<Renderer>().sharedMaterial = CreateToon(profile, "Counter", new Color(.2f, .65f, .9f), false);
                physics.PlacementPreviewMaterial = CreateToon(profile, "CounterPlacement", new Color(.2f, .65f, .9f, .5f), true);
                item.AddComponent(RequireType(profile.Info.KeyName, "CounterBehaviour"));
                profile.ResourceEntries.Add(SavePrefab(item, root + "/Art/Prefabs/Counter.prefab"));
            }
            finally { Object.DestroyImmediate(item); }
        }

        private static Material CreateToon(ModBuildProfile profile, string name, Color color, bool transparent)
        {
            EnsureFolder(profile.SourceRoot + "/Art/Materials");
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ModSDKEnvironment.ResolveAssetPath("Assets/Art/Shaders/Toon.shader")) ?? throw new InvalidDataException("SDK Toon Shader 缺失。");
            var material = new Material(shader) { name = name };
            WBS.Client.Logic.Gameplay.Graphics.ToonRenderSettings.Current.GetPreset(WBS.Client.Logic.Gameplay.Graphics.ToonStyle.Prop).Apply(material);
            material.SetColor("_BaseColor", color);
            if (transparent) material.SetFloat("_Surface", 1);
            WBS.Client.Logic.Gameplay.Graphics.ToonMaterialConverter.SetupMaterial(material);
            AssetDatabase.CreateAsset(material, profile.SourceRoot + "/Art/Materials/" + name + ".mat");
            return material;
        }
        private static void CreateUi(ModBuildProfile profile)
        {
            var panel = CreatePanel("ExamplePanel", "通过按钮主动打开页面、弹窗与覆盖层。", "打开弹窗");
            try
            {
                var script = panel.AddComponent(RequireType(profile.Info.KeyName, "ExamplePanel"));
                ConfigureMainUi(panel, script);
                profile.ResourceEntries.Add(SavePrefab(panel, profile.SourceRoot + "/Art/Prefabs/Panel.prefab"));
            }
            finally { Object.DestroyImmediate(panel); }
            // 根只提供全屏布局坐标；背景与射线目标限定在作者自己的局部内容中。
            var hud = new GameObject("ExampleHud", typeof(RectTransform));
            try
            {
                var rootRect = (RectTransform)hud.transform;
                rootRect.anchorMin = Vector2.zero; rootRect.anchorMax = Vector2.one;
                rootRect.offsetMin = rootRect.offsetMax = Vector2.zero;
                var content = CreatePanel("Content", "模组 HUD", null);
                content.transform.SetParent(hud.transform, false);
                var contentRect = (RectTransform)content.transform;
                contentRect.anchorMin = contentRect.anchorMax = Vector2.one;
                contentRect.pivot = Vector2.one;
                contentRect.sizeDelta = new Vector2(360, 100);
                contentRect.anchoredPosition = new Vector2(-24, -24);
                content.GetComponent<UnityEngine.UI.Image>().raycastTarget = false;
                var status = content.transform.Find("Status").GetComponent<TMP_Text>();
                var statusRect = (RectTransform)status.transform;
                statusRect.anchorMin = Vector2.zero; statusRect.anchorMax = Vector2.one;
                statusRect.offsetMin = new Vector2(16, 12); statusRect.offsetMax = new Vector2(-16, -12);
                var script = hud.AddComponent(RequireType(profile.Info.KeyName, "ExamplePanel"));
                SetRequiredField(script, "StatusText", status);
                profile.ResourceEntries.Add(SavePrefab(hud, profile.SourceRoot + "/Art/Prefabs/Hud.prefab"));
            }
            finally { Object.DestroyImmediate(hud); }
        }

        /// <summary>通过 Unity 序列化升级已有界面示例，保留 Prefab GUID 与制作配置身份。</summary>
        public static void RefreshUiExample(ModBuildProfile profile)
        {
            if (profile == null || profile.AuthoringKind != ModAuthoringKind.UI)
                throw new ArgumentException("请选择界面示例的 ModBuildProfile。");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                throw new InvalidOperationException("请在编辑模式并等待编译完成后升级界面示例。");
            var panelType = RequireType(profile.Info.KeyName, "ExamplePanel");
            string panelPath = profile.SourceRoot + "/Art/Prefabs/Panel.prefab";
            var panel = PrefabUtility.LoadPrefabContents(panelPath);
            try
            {
                var script = panel.GetComponent(panelType) ?? throw new InvalidDataException("界面示例的 ExamplePanel 组件缺失。");
                ConfigureMainUi(panel, script);
                if (PrefabUtility.SaveAsPrefabAsset(panel, panelPath) == null) throw new IOException("主界面 Prefab 保存失败。");
            }
            finally { PrefabUtility.UnloadPrefabContents(panel); }
            string hudPath = profile.SourceRoot + "/Art/Prefabs/Hud.prefab";
            var hud = PrefabUtility.LoadPrefabContents(hudPath);
            try
            {
                var script = hud.GetComponent(panelType) ?? throw new InvalidDataException("覆盖层示例的 ExamplePanel 组件缺失。");
                var status = hud.transform.Find("Content/Status")?.GetComponent<TMP_Text>();
                SetRequiredField(script, "StatusText", status);
                foreach (string fieldName in new[] { "ActionButton", "ViewButton", "OverlayButton", "CloseButton" })
                    panelType.GetField(fieldName)?.SetValue(script, null);
                if (PrefabUtility.SaveAsPrefabAsset(hud, hudPath) == null) throw new IOException("覆盖层 Prefab 保存失败。");
            }
            finally { PrefabUtility.UnloadPrefabContents(hud); }
            profile.Info.RequiredModApiVersion = "2.1.0";
            EditorUtility.SetDirty(profile);
            AssetDatabase.SaveAssetIfDirty(profile);
        }

        private static void ConfigureMainUi(GameObject panel, Component script)
        {
            SetRequiredField(script, "StatusText", panel.transform.Find("Status")?.GetComponent<TMP_Text>());
            var popup = panel.transform.Find("ActionButton")?.GetComponent<UnityEngine.UI.Button>()
                ?? throw new InvalidDataException("主界面示例的 ActionButton 缺失。");
            var view = GetOrCopyUiButton(panel, popup, "ViewButton");
            var overlay = GetOrCopyUiButton(panel, popup, "OverlayButton");
            var close = GetOrCopyUiButton(panel, popup, "CloseButton");
            var buttons = new[] { view, popup, overlay };
            var captions = new[] { "打开页面", "打开弹窗", "切换覆盖层" };
            for (int i = 0; i < buttons.Length; i++)
            {
                var rect = (RectTransform)buttons[i].transform;
                rect.anchorMin = new Vector2(.05f + i * .31f, .06f);
                rect.anchorMax = new Vector2(.33f + i * .31f, .25f);
                rect.offsetMin = rect.offsetMax = Vector2.zero;
                var caption = buttons[i].GetComponentInChildren<TMP_Text>(true)
                    ?? throw new InvalidDataException("示例按钮文字缺失。");
                caption.text = captions[i];
            }
            SetRequiredField(script, "ActionButton", popup);
            SetRequiredField(script, "ViewButton", view);
            SetRequiredField(script, "OverlayButton", overlay);
            // 关闭控件在制作阶段写入作者 Prefab，运行时只绑定本次会话。
            var closeRect = (RectTransform)close.transform;
            closeRect.anchorMin = closeRect.anchorMax = Vector2.one;
            closeRect.pivot = Vector2.one;
            closeRect.sizeDelta = new Vector2(80, 40);
            closeRect.anchoredPosition = new Vector2(-16, -16);
            var closeCaption = close.GetComponentInChildren<TMP_Text>(true)
                ?? throw new InvalidDataException("示例关闭按钮文字缺失。");
            closeCaption.text = "关闭";
            SetRequiredField(script, "CloseButton", close);
        }

        private static UnityEngine.UI.Button GetOrCopyUiButton(GameObject panel, UnityEngine.UI.Button source, string name)
        {
            var existing = panel.transform.Find(name);
            if (existing != null) return existing.GetComponent<UnityEngine.UI.Button>()
                ?? throw new InvalidDataException("已有示例按钮缺少 Button：" + name);
            var button = Object.Instantiate(source, panel.transform, false);
            button.name = name;
            return button;
        }

        public static GameObject CreatePanel(string name, string text, string buttonText)
        {
            var root = new GameObject(name, typeof(RectTransform), typeof(UnityEngine.UI.Image));
            var rect = (RectTransform)root.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = rect.offsetMax = Vector2.zero;
            root.GetComponent<UnityEngine.UI.Image>().color = new Color(.12f, .15f, .20f, .96f);
            var label = new GameObject("Status", typeof(RectTransform), typeof(TextMeshProUGUI)); label.transform.SetParent(root.transform, false);
            var labelRect = (RectTransform)label.transform; labelRect.anchorMin = new Vector2(.05f, .32f); labelRect.anchorMax = new Vector2(.95f, .95f); labelRect.offsetMin = labelRect.offsetMax = Vector2.zero;
            var font = AssetDatabase.LoadAssetAtPath<TMP_FontAsset>(ModSDKEnvironment.ResolveAssetPath("Assets/Art/Fonts/SourceHanSansSC-Regular SDF-Dynamic.asset"))
                ?? throw new InvalidDataException("SDK 中文字体缺失。");
            var tmp = label.GetComponent<TextMeshProUGUI>(); tmp.font = font; tmp.text = text; tmp.fontSize = 24; tmp.color = Color.white; tmp.raycastTarget = false; tmp.alignment = TextAlignmentOptions.Center;
            if (!string.IsNullOrEmpty(buttonText))
            {
                var button = new GameObject("ActionButton", typeof(RectTransform), typeof(UnityEngine.UI.Image), typeof(UnityEngine.UI.Button)); button.transform.SetParent(root.transform, false);
                var buttonRect = (RectTransform)button.transform; buttonRect.anchorMin = new Vector2(.25f, .06f); buttonRect.anchorMax = new Vector2(.75f, .25f); buttonRect.offsetMin = buttonRect.offsetMax = Vector2.zero;
                button.GetComponent<UnityEngine.UI.Image>().color = new Color(.25f, .40f, .60f, 1);
                var caption = new GameObject("Label", typeof(RectTransform), typeof(TextMeshProUGUI)); caption.transform.SetParent(button.transform, false);
                var captionRect = (RectTransform)caption.transform; captionRect.anchorMin = Vector2.zero; captionRect.anchorMax = Vector2.one; captionRect.offsetMin = captionRect.offsetMax = Vector2.zero;
                var captionText = caption.GetComponent<TextMeshProUGUI>(); captionText.font = font; captionText.text = buttonText; captionText.fontSize = 22; captionText.alignment = TextAlignmentOptions.Center; captionText.raycastTarget = false;
            }
            return root;
        }
        private static GameObject SavePrefab(GameObject source, string path)
        {
            if (File.Exists(path)) throw new IOException("示例资源已经存在，未覆盖：" + path);
            var prefab = PrefabUtility.SaveAsPrefabAsset(source, path) ?? throw new IOException("Prefab 保存失败：" + path);
            ModNetworkPrefabIdentity.Ensure(path);
            return prefab;
        }
        private static Type FindType(string key, string name) => AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == key + "ModEntry")?.GetType("WBS.Client.Mod." + key + "." + name);
        private static Type RequireType(string key, string name) => FindType(key, name) ?? throw new TypeLoadException("作者类没有编译：" + name);
        private static void SetRequiredField(Component component, string name, Object value)
        {
            var field = component.GetType().GetField(name) ?? throw new MissingFieldException(component.GetType().FullName, name);
            if (value == null || !field.FieldType.IsInstanceOfType(value)) throw new InvalidDataException("示例字段类型或资源不匹配：" + name);
            field.SetValue(component, value);
        }
        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            string parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent); AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }

        private static string EntrySource(ModAuthoringKind kind, string key, string gameplayUuid)
        {
            string registration = kind switch
            {
                ModAuthoringKind.Gameplay => "api.RegisterGameplay(new ModGameplayDefinition { Info = new WBS.Client.Common.WBG.WBGInfo { UUID = \"" + gameplayUuid + "\", KeyName = \"" + key + "\", Version = \"1.0.0\" }, SettingsType = typeof(" + key + "Settings), ControllerPrefabAddress = \"Art/Prefabs/Controller.prefab\", ConfigAddress = \"StaticData/Config.asset\", RulePanelAddress = \"Art/Prefabs/RulePanel.prefab\", PadAppsAddress = \"StaticData/PadApps.asset\", Maps = new System.Collections.Generic.List<ModMapDefinition> { new() { MapId = \"1\", PrefabAddress = \"Art/Prefabs/Map1.prefab\" } } });",
                ModAuthoringKind.Item => GameplayRegistration(key, gameplayUuid) + "api.RegisterItem(new ModItemDefinition { ItemId = \"Counter\", InfoAddress = \"StaticData/Counter.asset\", DataModelType = typeof(CounterData), BehaviourPrefabAddress = \"Art/Prefabs/Counter.prefab\" });",
                ModAuthoringKind.UI => "api.Ui.RegisterMenuPanel(\"Example\", \"模组示例面板\", \"Art/Prefabs/Panel.prefab\");",
                _ => throw new ArgumentException("人物使用自动登记入口。")
            };
            return "using WBS.Client.Common.Mod;\nusing WBS.Client.Logic.ModSupport;\nnamespace WBS.Client.Mod." + key +
                " { public sealed class " + key + "Mod : ModBase { public override bool OnLoad() { var api = ModApi.For(Context); " + registration + " return true; } } }\n";
        }

        private static string GameplayRegistration(string key, string uuid) =>
            "api.RegisterGameplay(new ModGameplayDefinition { Info = new WBS.Client.Common.WBG.WBGInfo { UUID = \"" + uuid + "\", KeyName = \"" + key + "\", Version = \"1.0.0\" }, SettingsType = typeof(" + key + "Settings), ControllerPrefabAddress = \"Art/Prefabs/Controller.prefab\", ConfigAddress = \"StaticData/Config.asset\", RulePanelAddress = \"Art/Prefabs/RulePanel.prefab\", PadAppsAddress = \"StaticData/PadApps.asset\", Maps = new System.Collections.Generic.List<ModMapDefinition> { new() { MapId = \"1\", PrefabAddress = \"Art/Prefabs/Map1.prefab\" } } });";

        private const string UiSource = @"
using TMPro;
using UnityEngine;
using UnityEngine.UI;
using WBS.Client.Logic.ModSupport;
using WBS.Client.Logic.UI.ModUi;

namespace WBS.Client.Mod.__KEY__
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
            if (StatusText == null) throw new System.InvalidOperationException(""请在 Prefab 中绑定 StatusText。"");
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
            owner.Own(ModApi.For(owner.Context).Ui.OpenView(""Art/Prefabs/Hud.prefab""));
            count++;
            Refresh();
        }

        private void OpenPopup()
        {
            if (owner == null || owner.IsClosed) return;
            owner.Own(ModApi.For(owner.Context).Ui.OpenPopup(""模组界面演示"",
                ""确认后打开独立页面并切换覆盖层。取消会保留主界面。"", () =>
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
                if (canvas == null) throw new System.InvalidOperationException(""模组界面必须位于 Canvas 下。"");
                // 不入栈、不带背景；HUD 和提示层的锚点、位置与交互区域都保存在作者 Prefab 中。
                overlay = ModApi.For(owner.Context).Ui.OpenPanel(""Art/Prefabs/Hud.prefab"", canvas.transform,
                    useStack: false, useBg: false);
                owner.Own(overlay);
            }
            Refresh();
        }

        private void Refresh()
        {
            if (StatusText != null) StatusText.text = ""打开页面："" + count + "" 次\n覆盖层："" +
                (overlay != null && !overlay.IsClosed ? ""已打开"" : ""已关闭"");
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
";
        private const string ItemSource = @"
namespace WBS.Client.Mod.__KEY__
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
            result.PresentationVariantId = ""Authorized"";
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
            new() { new() { label = ""拾取计数器"", action = () => WBS.Client.Logic.Gameplay.Bag.BagItemManager.Instance.TryPickItem(Uid) } };
        public override System.Collections.Generic.List<WBS.Client.Logic.Gameplay.Item.ItemAction> GetHandActionList() =>
            new() { new() { label = ""使用计数器（"" + uses + ""）"", action = () => CmdUse() } };
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
        [Mirror.ClientRpc] private void RpcUsed(int value) { UnityEngine.Debug.Log(""计数器已使用："" + value); }
    }
}
";
    }
}
