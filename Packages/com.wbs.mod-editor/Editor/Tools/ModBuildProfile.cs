using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;
using WBS.Client.Common.Mod;
using WBS.Client.Editor.CharacterAuthoring;

namespace WBS.Client.Editor.ModSDK
{
    public enum ModBuildMode { [InspectorName("模型模组")] Model, [InspectorName("自定义入口")] Custom }
    public enum ModAuthoringKind
    {
        [InspectorName("人物模型")] Model, [InspectorName("玩法")] Gameplay,
        [InspectorName("物品")] Item, [InspectorName("界面")] UI
    }

    /// <summary>可共享的制作配置；项目内输出目录使用相对路径，构建请求可临时覆盖。</summary>
    [CreateAssetMenu(menuName = "WBS/模组/打包配置", fileName = "ModBuildProfile")]
    public sealed class ModBuildProfile : ScriptableObject
    {
        public ModInfo Info = new() { Version = "1.0.0", Dependencies = new List<string>() };
        public ModBuildMode Mode;
        [Tooltip("制作入口分类，不限制自定义入口可以包含的逻辑。")]
        public ModAuthoringKind AuthoringKind;
        [FormerlySerializedAs("DevelopmentHostPath")]
        [Tooltip("配套游戏 WBS.exe 路径；开发调试使用 --dev，不写入发布包或资源地址。")]
        public string GameExecutablePath;
        [Tooltip("生成调试代码并将 PDB 放在作者 DLL 旁；正式发布时关闭。发布快照会排除符号。")]
        public bool DebugBuild;
        [Tooltip("模组资源目录，必须为 Assets/Mod/{KeyName}。其中可打包资源使用完整路径作为地址。")]
        public string SourceRoot;
        [Tooltip("发布输出根目录；项目相对路径可随配置跨机器使用，构建请求可覆盖此值。")]
        public string OutputRoot = "Build/Mods";
        [Tooltip("明确选择的资源入口；为空时打包资源根中的全部可用资源。依赖由 Addressables 自动收集。")]
        public List<UnityEngine.Object> ResourceEntries = new();
        [Tooltip("已有完整人物的制作配置；只检查与登记已生成成品，不会在打包过程中生成或刷新人物。")]
        public List<CharacterAuthoringRecipe> CharacterRecipes = new();
        [Tooltip("模型模式中需要登记的完整人物 Prefab；为空时使用角色成品目录中的所有 Prefab。")]
        public List<GameObject> ModelPrefabs = new();
        [Tooltip("除固定入口外，允许导出的本模组 Player 程序集名称，不填写 .dll 后缀。")]
        public List<string> AdditionalAssemblyNames = new();
        [Tooltip("明确随包分发的第三方 DLL；不会自动复制宿主运行库。")]
        public List<string> ExternalDlls = new();
        [Tooltip("复制为发布包 data/ 的目录；为空时使用资源根下的 data/。")]
        public string DataSourceDirectory;
        [Tooltip("模组图标源文件；为空时按 Info.IconPath 从资源根解析。")]
        public string IconSourcePath;
    }

    public sealed class ModBuildRequest
    {
        public ModBuildProfile Profile;
        /// <summary>为空时使用配置中的输出目录；相对路径从当前 Unity 项目根解析。</summary>
        public string OutputRoot;
    }
}
