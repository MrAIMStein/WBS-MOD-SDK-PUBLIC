using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace WBS.Client.Editor.ModSDK
{
    /// <summary>提供制作配置、只读检查与发布结果；构建逻辑全部交由共用服务。</summary>
    public sealed class ModBuildWindow : EditorWindow
    {
        [SerializeField] private ModBuildProfile profile;
        private string outputRoot;
        private string status = "选择打包配置后检查资源与入口。";
        private string[] errors = Array.Empty<string>();
        private ModBuildReport report;
        private bool reportIsBuild;
        private bool building;
        private Vector2 scroll;
        private UnityEditor.Editor inspector;

        [MenuItem("WBS/模组/打包工具")]
        public static void Open() => GetWindow<ModBuildWindow>("模组打包");

        private void OnEnable()
        {
            outputRoot = ProfileOutputRoot();
            if (profile != null) inspector = UnityEditor.Editor.CreateEditor(profile);
        }
        private void OnDisable() { if (inspector != null) DestroyImmediate(inspector); }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("通用模组打包", EditorStyles.boldLabel);
            string sdkVersion;
            try { sdkVersion = ModSDKEnvironment.InstalledVersion; }
            catch { sdkVersion = "读取失败"; }
            EditorGUILayout.LabelField("SDK " + sdkVersion + " · Windows x64");
            using (new EditorGUI.DisabledScope(building || ModBuildService.IsBusy))
            {
                ModBuildProfile selected = (ModBuildProfile)EditorGUILayout.ObjectField("制作配置", profile, typeof(ModBuildProfile), false);
                if (selected != profile)
                {
                    profile = selected;
                    if (inspector != null) DestroyImmediate(inspector);
                    inspector = profile != null ? UnityEditor.Editor.CreateEditor(profile) : null;
                    outputRoot = ProfileOutputRoot();
                    errors = Array.Empty<string>();
                    report = null;
                    reportIsBuild = false;
                }
                if (GUILayout.Button("新建打包配置")) CreateProfile();
                if (GUILayout.Button("按类型创建模组")) ModAuthoringWizard.Open();
                EditorGUILayout.BeginHorizontal();
                EditorGUI.BeginChangeCheck();
                string selectedOutput = EditorGUILayout.DelayedTextField("输出根目录", outputRoot);
                if (EditorGUI.EndChangeCheck()) SetOutputRoot(selectedOutput);
                if (GUILayout.Button("选择", GUILayout.Width(55)))
                {
                    string initialDirectory;
                    try { initialDirectory = ModBuildService.ResolveOutputRoot(Request()); }
                    catch { initialDirectory = ModBuildService.ProjectRoot; }
                    string folder = EditorUtility.OpenFolderPanel("选择模组输出根目录", initialDirectory, "");
                    if (!string.IsNullOrEmpty(folder)) SetOutputRoot(ModBuildService.ToPortableOutputRoot(folder));
                }
                EditorGUILayout.EndHorizontal();
                scroll = EditorGUILayout.BeginScrollView(scroll);
                if (inspector != null) inspector.OnInspectorGUI();
                if (profile != null) outputRoot = ProfileOutputRoot();
                EditorGUILayout.EndScrollView();
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("检查"))
                {
                    report = ModBuildService.Validate(Request());
                    reportIsBuild = false;
                    errors = report.Errors;
                    status = errors.Length == 0 ? "检查通过；打包将再次检查并校验实际产物。" : "检查未通过，请处理下列问题。";
                }
                if (GUILayout.Button("打包发布目录")) BuildSelected();
                EditorGUILayout.EndHorizontal();
                if (profile != null)
                {
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField("配套游戏", GUILayout.Width(70));
                    EditorGUILayout.SelectableLabel(profile.GameExecutablePath ?? "尚未选择", GUILayout.Height(20));
                    if (GUILayout.Button("选择 exe", GUILayout.Width(85)))
                    {
                        string path = EditorUtility.OpenFilePanel("选择 Windows x64 配套游戏", "", "exe");
                        if (!string.IsNullOrEmpty(path)) { Undo.RecordObject(profile, "选择配套游戏"); profile.GameExecutablePath = path; EditorUtility.SetDirty(profile); }
                    }
                    EditorGUILayout.EndHorizontal();
                    if (GUILayout.Button("打包并启动调试")) BuildSelected(true);
                }
            }
            EditorGUILayout.HelpBox(status, errors.Length == 0 ? MessageType.Info : MessageType.Error);
            foreach (string error in errors) EditorGUILayout.HelpBox(error, MessageType.Error);
            foreach (string warning in report?.Warnings ?? Array.Empty<string>()) EditorGUILayout.HelpBox(warning, MessageType.Warning);
            if (!string.IsNullOrEmpty(report?.OutputDirectory))
            {
                EditorGUILayout.LabelField(reportIsBuild ? "成品目录" : "预期输出目录");
                EditorGUILayout.SelectableLabel(report.OutputDirectory, GUILayout.Height(20));
                if (reportIsBuild && report.Success && GUILayout.Button("打开成品目录")) EditorUtility.RevealInFinder(report.OutputDirectory);
            }
        }

        private ModBuildRequest Request() => new() { Profile = profile, OutputRoot = outputRoot };

        private string ProfileOutputRoot() => string.IsNullOrWhiteSpace(profile?.OutputRoot) ? "Build/Mods" : profile.OutputRoot;

        private void SetOutputRoot(string value)
        {
            // 目录选择及完整路径输入尽量保存为项目相对路径；未完成的输入交由预检报告。
            try { outputRoot = string.IsNullOrWhiteSpace(value) ? "Build/Mods" : ModBuildService.ToPortableOutputRoot(value); }
            catch { outputRoot = value; }
            if (profile == null || profile.OutputRoot == outputRoot) return;
            Undo.RecordObject(profile, "修改模组输出目录");
            profile.OutputRoot = outputRoot;
            EditorUtility.SetDirty(profile);
        }

        private async void BuildSelected(bool debug = false)
        {
            ModBuildProfile debugProfile = null;
            ModBuildRequest request = Request();
            if (debug)
            {
                try { GameDevelopmentLauncher.ReadGameInfo(GameDevelopmentLauncher.ResolveGame(profile?.GameExecutablePath)); }
                catch (Exception error) { errors = new[] { error.Message }; status = "配套游戏配置未通过。"; return; }
                debugProfile = Instantiate(profile);
                debugProfile.DebugBuild = true;
                request = new ModBuildRequest { Profile = debugProfile, OutputRoot = "Library/ModSDK/DebugBuilds/" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) };
            }
            report = ModBuildService.Validate(request);
            reportIsBuild = false;
            errors = report.Errors;
            if (errors.Length != 0) { status = "检查未通过。"; if (debugProfile != null) DestroyImmediate(debugProfile); return; }
            building = true;
            report = null;
            try
            {
                report = await ModBuildService.BuildAsync(request, message => { status = message; Repaint(); });
                reportIsBuild = true;
                errors = report.Errors;
                if (!report.Success) { status = "打包失败，旧成品保持原样。"; return; }
                status = "打包完成。选择此成品目录即可本地安装或上传创意工坊。";
                if (debug)
                {
                    GameDevelopmentLauncher.Launch(profile, report);
                    status = "游戏开发模式已启动。修改后重新打包并重启游戏；会话记录包含进程日志。";
                }
            }
            catch (Exception exception)
            {
                errors = new[] { exception.Message };
                status = "打包失败，旧成品保持原样。";
                Debug.LogException(exception);
            }
            finally { if (debugProfile != null) DestroyImmediate(debugProfile); building = false; Repaint(); }
        }

        private void CreateProfile()
        {
            string path = EditorUtility.SaveFilePanelInProject("新建模组打包配置", "ModBuildProfile", "asset", "制作配置建议保存到模组的 Editor 目录。");
            if (string.IsNullOrEmpty(path)) return;
            if (!path.Replace('\\', '/').Contains("/Editor/"))
            { status = "请把制作配置保存到 Editor 目录，避免配置进入发布资源。"; return; }
            var created = CreateInstance<ModBuildProfile>();
            created.Info.UUID = Guid.NewGuid().ToString("D");
            AssetDatabase.CreateAsset(created, path);
            profile = created;
            outputRoot = ProfileOutputRoot();
            if (inspector != null) DestroyImmediate(inspector);
            inspector = UnityEditor.Editor.CreateEditor(profile);
            Selection.activeObject = profile;
        }
    }
}
