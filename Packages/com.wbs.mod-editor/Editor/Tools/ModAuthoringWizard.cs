using System;
using UnityEditor;
using UnityEngine;

namespace WBS.Client.Editor.ModSDK
{
    public sealed class ModAuthoringWizard : EditorWindow
    {
        private ModAuthoringKind kind;
        private string keyName = "MyMod";
        private string displayName = "我的模组";
        private string author = "";
        private string status;
        [MenuItem("WBS/模组/新建模组")]
        public static void Open() => GetWindow<ModAuthoringWizard>("新建模组");
        private void OnGUI()
        {
            EditorGUILayout.LabelField("使用 SDK 创建作者目录", EditorStyles.boldLabel);
            kind = (ModAuthoringKind)EditorGUILayout.EnumPopup("模组类型", kind);
            keyName = EditorGUILayout.TextField("英文目录标识", keyName);
            displayName = EditorGUILayout.TextField("模组名称", displayName);
            author = EditorGUILayout.TextField("作者", author);
            EditorGUILayout.HelpBox("人物模型通过制作配方生成；玩法、物品和界面会创建可编译的 C# 入口与所需资源。生成后在打包工具中校验并启动游戏开发模式。", MessageType.Info);
            using (new EditorGUI.DisabledScope(EditorApplication.isCompiling || EditorApplication.isUpdating))
            {
                if (GUILayout.Button("创建模组"))
                {
                    try
                    {
                        var profile = ModAuthoringScaffold.Create(kind, keyName, displayName, author);
                        Selection.activeObject = profile;
                        status = "目录和入口已创建；编译完成后自动生成并绑定示例资源。";
                    }
                    catch (Exception error) { status = error.Message; Debug.LogException(error); }
                }
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.Info);
        }
    }
}
