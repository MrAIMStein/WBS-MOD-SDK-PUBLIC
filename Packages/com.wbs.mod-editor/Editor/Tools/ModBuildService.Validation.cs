using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using WBS.Client.Common.Mod;
using WBS.Client.Editor.CharacterAuthoring;

namespace WBS.Client.Editor.ModSDK
{
    public static partial class ModBuildService
    {
        private static readonly Regex Token = new("^[A-Za-z][A-Za-z0-9_]{0,63}$");
        private static readonly Regex AssemblyToken = new("^[A-Za-z_][A-Za-z0-9_.-]{0,95}$");
        internal sealed class BuildInputs
        {
            internal ModBuildProfile Profile;
            internal ModInfo Info;
            internal string SourceRoot, OutputRoot, FinalDirectory, DataDirectory, IconPath;
            internal string[] Assets, ModelIds, SharedDependencies, AuxiliaryAssemblies, ExternalDlls, Warnings;
            internal string[] EditorWovenTypes = Array.Empty<string>();
        }

        internal static BuildInputs Inspect(ModBuildRequest request, List<string> errors)
        {
            var result = new BuildInputs();
            if (request?.Profile == null) { errors.Add("请选择模组打包配置。"); return result; }
            result.Profile = request.Profile;
            if (request.Profile.Info == null) { errors.Add("缺少模组信息。"); return result; }
            result.Info = JsonUtility.FromJson<ModInfo>(JsonUtility.ToJson(request.Profile.Info));
            // 协议来自当前 SDK，不能让作者无意把新 API 包声明为旧协议或漏填。
            result.Info.RequiredModApiVersion = ModSDKEnvironment.InstalledModApiVersion;
            ModInfo info = result.Info;
            if (!ModCompatibilityManifest.IsIdentifier(info.UUID)) errors.Add("模组 UUID 格式无效。");
            if (request.Profile.Mode == ModBuildMode.Model && !Guid.TryParse(info.UUID, out _))
                errors.Add("模型模组必须使用长期稳定的 GUID，与角色制作配置的 UUID 保持一致。");
            if (!Token.IsMatch(info.KeyName ?? "")) errors.Add("KeyName 必须以字母开头，仅使用字母、数字和下划线，最多 64 位。");
            if (string.IsNullOrWhiteSpace(info.Name)) errors.Add("请填写模组名称。");
            if (string.IsNullOrWhiteSpace(info.Version) || !ModCompatibilityManifest.IsVersion(info.Version)) errors.Add("请填写有效的模组版本。");
            if (!string.IsNullOrWhiteSpace(info.MinGameVersion) && !IsNumericVersion(info.MinGameVersion))
                errors.Add("最低游戏版本必须为一至四段数字点分版本。");
            var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (string dependency in info.Dependencies ?? new List<string>())
                if (!ModCompatibilityManifest.IsIdentifier(dependency) || dependency == info.UUID || !dependencyIds.Add(dependency))
                    errors.Add("依赖标识无效、自依赖或重复：" + dependency);
            if (!Enum.IsDefined(typeof(ModBuildMode), request.Profile.Mode)) errors.Add("模组构建模式无效。");
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling || EditorApplication.isUpdating)
                errors.Add("请在编辑模式并完成导入、编译后构建。");
            if (EditorUtility.scriptCompilationFailed) errors.Add("项目存在编译错误。");
            if (IsBusy) errors.Add("已有模组构建正在进行。");
            if (EditorUserBuildSettings.activeBuildTarget != BuildTarget.StandaloneWindows64)
                errors.Add("首期模组仅支持 Windows x64，请先切换构建目标。");
            for (int i = 0; i < UnityEngine.SceneManagement.SceneManager.sceneCount; i++)
                if (UnityEngine.SceneManagement.SceneManager.GetSceneAt(i).isDirty)
                    errors.Add("当前场景有未保存修改，请保存后构建模组。");
            if (errors.Count != 0) return result;

            result.SourceRoot = (string.IsNullOrWhiteSpace(request.Profile.SourceRoot)
                ? "Assets/Mod/" + info.KeyName : request.Profile.SourceRoot).Replace('\\', '/').TrimEnd('/');
            if (result.SourceRoot != "Assets/Mod/" + info.KeyName || !AssetDatabase.IsValidFolder(result.SourceRoot))
                errors.Add("资源根必须是已存在的 Assets/Mod/" + info.KeyName + " 目录。");
            EnsurePlainPath(Path.Combine(ProjectRoot, result.SourceRoot));
            result.OutputRoot = ResolveOutputRoot(request);
            EnsurePlainPath(result.OutputRoot);
            if (File.Exists(result.OutputRoot)) errors.Add("输出根路径已被文件占用。");
            if (IsWithin(result.OutputRoot, Application.dataPath) || IsWithin(result.OutputRoot, Path.Combine(ProjectRoot, "Packages")))
                errors.Add("发布输出不能位于 Assets 或 Packages 内。");
            result.FinalDirectory = Path.Combine(result.OutputRoot, info.KeyName + "-" + info.Version);
            if (Directory.Exists(result.FinalDirectory) || File.Exists(result.FinalDirectory)) errors.Add("发布目录已存在，禁止覆盖：" + result.FinalDirectory);
            if (errors.Count != 0) return result;

            var warnings = new List<string>();
            if (request.Profile.DebugBuild) warnings.Add("当前为调试包：PDB 放在 DLL 旁供游戏开发调试读取，创意工坊发布快照会排除符号；正式分发请关闭 DebugBuild 重新构建。");
            string[] recipeOutputs = GetRecipeOutputs(request.Profile, info, result.SourceRoot, errors, warnings);
            result.Assets = SelectResourceAssets(request.Profile, result.SourceRoot, recipeOutputs, errors);
            if (result.Assets.Length == 0 && request.Profile.Mode == ModBuildMode.Model) errors.Add("模型模组资源根中没有可打包资源。");
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in result.Assets)
                if (!unique.Add(path)) errors.Add("资源地址存在大小写冲突：" + path);
            result.ModelIds = FindModelIds(request.Profile, result.SourceRoot, result.Assets, recipeOutputs, errors);
            result.AuxiliaryAssemblies = (request.Profile.AdditionalAssemblyNames ?? new List<string>()).ToArray();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { info.KeyName + "ModEntry" };
            foreach (string name in result.AuxiliaryAssemblies)
                if (!IsExportableAssembly(name) || !names.Add(name)) errors.Add("辅助程序集名称无效、重复或属于宿主：" + name);
            result.ExternalDlls = (request.Profile.ExternalDlls ?? new List<string>()).Select(path => FullSourcePath(path)).ToArray();
            foreach (string file in result.ExternalDlls)
            {
                EnsurePlainPath(file);
                string name = Path.GetFileNameWithoutExtension(file);
                if (!File.Exists(file) || !file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                    !IsExportableAssembly(name) || !names.Add(name)) errors.Add("第三方 DLL 无效、重复或属于宿主：" + file);
                else
                {
                    DllMetadata metadata = ReadDllMetadata(file);
                    if (metadata.References.Any(reference => reference.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase)))
                        errors.Add("第三方运行时 DLL 引用了 UnityEditor，不能发布：" + file);
                    string assetPath = AssetDatabase.GetAllAssetPaths().Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                        .FirstOrDefault(path => string.Equals(FullSourcePath(path), file, StringComparison.OrdinalIgnoreCase));
                    if (assetPath != null && AssetImporter.GetAtPath(assetPath) is PluginImporter importer &&
                        !ModSDKEnvironment.IsWindowsPlayerPlugin(importer))
                        errors.Add("第三方 DLL 未启用 Windows x64 Player 兼容，不能发布：" + assetPath);
                }
            }
            result.SharedDependencies = ValidateDependencies(result.Assets, result.SourceRoot, errors, result.ExternalDlls);
            ValidateMaterialsAndShaders(result.Assets, errors, warnings);
            ValidateNetworkPrefabs(result.Assets, errors);
            ValidateDependencyAssemblies(result, errors);
            if (request.Profile.Mode == ModBuildMode.Model && result.AuxiliaryAssemblies.Length != 0)
                errors.Add("模型模式不导出自定义 Player 程序集；需要行为代码时请选择自定义模式。");
            if (request.Profile.Mode == ModBuildMode.Custom)
            {
                ValidateCustomAssemblyOwnership(result, errors);
                if (errors.Count == 0)
                {
                    var editor = UnityEditor.Compilation.CompilationPipeline.GetAssemblies(UnityEditor.Compilation.AssembliesType.Editor);
                    string[] namesToExport = new[] { info.KeyName + "ModEntry" }.Concat(result.AuxiliaryAssemblies).ToArray();
                    string[] files = namesToExport.Select(name => editor.SingleOrDefault(assembly => assembly.name == name)?.outputPath).ToArray();
                    if (files.Any(file => string.IsNullOrEmpty(file) || !File.Exists(file))) errors.Add("作者 Editor 程序集尚未生成，请等待 Unity 导入并完成编译。");
                    else
                    {
                        try { result.EditorWovenTypes = ValidateNetworkAssemblies(files.Concat(result.ExternalDlls).ToArray(), GetNetworkReferencePaths(editor)); }
                        catch (Exception exception) { errors.Add("Editor 网络代码预检失败：" + exception); }
                        try { warnings.AddRange(GetHostContractWarnings(files.Concat(result.ExternalDlls))); }
                        catch (Exception exception) { warnings.Add("无法完成宿主稳定接口静态检查，允许继续打包，请自行检查直接接口引用：" + exception.Message); }
                    }
                }
            }

            string data = string.IsNullOrWhiteSpace(request.Profile.DataSourceDirectory)
                ? Path.Combine(result.SourceRoot, "data") : request.Profile.DataSourceDirectory;
            result.DataDirectory = FullSourcePath(data);
            EnsurePlainPath(result.DataDirectory);
            if (!string.IsNullOrWhiteSpace(request.Profile.DataSourceDirectory) && !Directory.Exists(result.DataDirectory))
                errors.Add("数据源目录不存在。");
            if (Directory.Exists(result.DataDirectory) && IsWithin(result.OutputRoot, result.DataDirectory)) errors.Add("输出目录不能位于数据源内。");
            string icon = request.Profile.IconSourcePath;
            if (string.IsNullOrWhiteSpace(icon) && !string.IsNullOrWhiteSpace(info.IconPath)) icon = Path.Combine(result.SourceRoot, info.IconPath);
            result.IconPath = string.IsNullOrWhiteSpace(icon) ? null : FullSourcePath(icon);
            if (result.IconPath != null)
            {
                EnsurePlainPath(result.IconPath);
                if (!ModPublishPackage.TryValidatePreview(result.IconPath, out string key)) errors.Add("模组图标检查失败：" + key);
                info.IconPath = "icon" + Path.GetExtension(result.IconPath).ToLowerInvariant();
            }
            else info.IconPath = "";
            result.Warnings = warnings.Distinct().ToArray();
            return result;
        }

        internal static void ValidateMaterialsAndShaders(string[] assets, List<string> errors, List<string> warnings)
        {
            var checkedMaterials = new HashSet<int>();
            var checkedShaders = new HashSet<int>();
            foreach (string path in AssetDatabase.GetDependencies(assets, true))
            {
                Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
                string extension = Path.GetExtension(path).ToLowerInvariant();
                // 原生 .asset 与模型导入文件也可含材质子资产，不能仅按 .mat 后缀检查。
                if (type != typeof(Material) && type != typeof(Shader) && type != typeof(GameObject) && extension != ".asset") continue;
                foreach (UnityEngine.Object asset in AssetDatabase.LoadAllAssetsAtPath(path))
                {
                    if (asset is Material material && checkedMaterials.Add(material.GetInstanceID()))
                    {
                        string label = path + "（材质 " + material.name + "）";
                        if (material.shader == null) { errors.Add("材质缺少 Shader：" + label); continue; }
                        CheckShader(material.shader, label);
                    }
                    else if (asset is Shader shader) CheckShader(shader, path);
                }
            }

            void CheckShader(Shader shader, string label)
            {
                if (!checkedShaders.Add(shader.GetInstanceID())) return;
                if (!shader.isSupported)
                    errors.Add("Shader 不受当前编辑器图形环境支持，请修复后打包：" + label + "（" + shader.name + "）");
                if (shader.name == "UGL/Toon")
                    warnings.Add("资源仍使用旧版 UGL/Toon：" + label + "。可在编辑器中迁移到 WBS/Toon，并在预览中手工检查和调整；打包及运行时不会自动转换。");
            }
        }

        internal static bool IsResourceRootAsset(string path, string root)
        {
            if (!path.StartsWith(root + "/", StringComparison.Ordinal) || AssetDatabase.IsValidFolder(path)) return false;
            string relative = path.Substring(root.Length + 1);
            if (relative.Split('/').Any(segment => segment == "Editor") || relative.StartsWith("data/", StringComparison.OrdinalIgnoreCase)
                || relative.StartsWith("dll/", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("autoload/", StringComparison.OrdinalIgnoreCase)) return false;
            string extension = Path.GetExtension(path).ToLowerInvariant();
            if (new[] { ".cs", ".asmdef", ".asmref", ".dll", ".pdb", ".mdb", ".meta", ".md" }.Contains(extension)) return false;
            Type type = AssetDatabase.GetMainAssetTypeAtPath(path);
            return type != null && type != typeof(DefaultAsset) && type != typeof(MonoScript) &&
                type != typeof(ModBuildProfile) && type != typeof(CharacterAuthoringRecipe);
        }

        internal static string ResolveOutputRoot(ModBuildRequest request)
        {
            string output = !string.IsNullOrWhiteSpace(request?.OutputRoot) ? request.OutputRoot : request?.Profile?.OutputRoot;
            if (string.IsNullOrWhiteSpace(output)) output = "Build/Mods";
            return Path.GetFullPath(Path.IsPathRooted(output) ? output : Path.Combine(ProjectRoot, output));
        }

        internal static string ToPortableOutputRoot(string path)
        {
            string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ProjectRoot, path));
            return IsWithin(full, ProjectRoot) ? Path.GetRelativePath(ProjectRoot, full).Replace('\\', '/') : full;
        }

        internal static string[] SelectResourceAssets(ModBuildProfile profile, string root, string[] recipeOutputs, List<string> errors)
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            if (profile.ResourceEntries?.Count > 0)
            {
                foreach (UnityEngine.Object entry in profile.ResourceEntries) Add(AssetDatabase.GetAssetPath(entry), "显式资源入口");
            }
            else
            {
                foreach (string path in AssetDatabase.GetAllAssetPaths().Where(path => IsResourceRootAsset(path, root))) paths.Add(path);
            }
            // 人物选择本身也是显式资源入口，不能因资源列表遗漏而只登记一个不存在的地址。
            foreach (GameObject prefab in profile.ModelPrefabs ?? new List<GameObject>()) Add(AssetDatabase.GetAssetPath(prefab), "模型成品");
            foreach (string path in recipeOutputs ?? Array.Empty<string>()) Add(path, "人物制作成品");
            return paths.OrderBy(path => path, StringComparer.Ordinal).ToArray();

            void Add(string path, string label)
            {
                if (string.IsNullOrEmpty(path) || !IsResourceRootAsset(path, root))
                    errors.Add(label + "必须是本模组资源根下可打包的已保存资产：" + path);
                else paths.Add(path);
            }
        }

        internal static string[] GetRecipeOutputs(ModBuildProfile profile, ModInfo info, string root, List<string> errors, List<string> warnings)
        {
            var outputs = new HashSet<string>(StringComparer.Ordinal);
            foreach (CharacterAuthoringRecipe recipe in profile.CharacterRecipes ?? new List<CharacterAuthoringRecipe>())
            {
                if (recipe == null) { errors.Add("人物制作清单包含空配置。"); continue; }
                string label = AssetDatabase.GetAssetPath(recipe);
                if (string.IsNullOrEmpty(label)) label = recipe.name;
                label = "人物制作配置 " + label + "：";
                if (recipe.Destination != CharacterAuthoringDestination.Mod || recipe.ModKeyName != info.KeyName || recipe.ModUuid != info.UUID)
                { errors.Add(label + "目的地、模组 UUID 或 KeyName 与打包配置不一致。"); continue; }
                try
                {
                    string path = CharacterAuthoringBuilder.GetOutputPath(recipe);
                    string expected = root + "/Art/Prefabs/Character_Multi/" + recipe.ModelId + ".prefab";
                    if (path != expected || recipe.LastOutputPath != path || string.IsNullOrEmpty(recipe.OutputGuid) ||
                        AssetDatabase.GUIDToAssetPath(recipe.OutputGuid) != path || AssetDatabase.LoadAssetAtPath<GameObject>(path) == null)
                        errors.Add(label + "已生成成品缺失或输出身份不一致，请先在人物制作窗口完成生成。");
                    else
                    {
                        EnsurePlainPath(FullSourcePath(path));
                        if (!outputs.Add(path)) errors.Add(label + "与清单中的其他制作配置指向同一人物成品。");
                    }
                    CharacterAuthoringReport validation = CharacterAuthoringBuilder.Validate(recipe);
                    if (!validation.Success && (validation.Errors == null || validation.Errors.Length == 0))
                        errors.Add(label + "人物制作检查未通过。");
                    foreach (string error in validation.Errors ?? Array.Empty<string>()) errors.Add(label + error);
                    foreach (string warning in validation.Warnings ?? Array.Empty<string>()) warnings.Add(label + warning);
                }
                catch (Exception exception) { errors.Add(label + exception.Message); }
            }
            return outputs.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        internal static string[] ValidateDependencies(string[] assets, string root, List<string> errors, string[] externalDlls = null)
        {
            var shared = new SortedSet<string>(StringComparer.Ordinal);
            foreach (string path in AssetDatabase.GetDependencies(assets, true))
            {
                if (path.StartsWith(root + "/", StringComparison.Ordinal)) continue;
                if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    (externalDlls ?? Array.Empty<string>()).Contains(FullSourcePath(path), StringComparer.OrdinalIgnoreCase)) continue;
                if (ModSDKEnvironment.IsSharedAsset(path)) { shared.Add(path); continue; }
                if (path.StartsWith("Packages/com.unity.", StringComparison.Ordinal) ||
                    path == "Resources/unity_builtin_extra" || path == "Library/unity default resources") continue;
                errors.Add("资源引用了未列入 SDK 的工程内容，请复制到本模组或使用 SDK 共享资产：" + path);
            }
            return shared.ToArray();
        }

        private static string[] FindModelIds(ModBuildProfile profile, string root, string[] assets, string[] recipeOutputs, List<string> errors)
        {
            string directory = root + "/Art/Prefabs/Character_Multi/";
            string[] paths = profile.ModelPrefabs?.Count > 0 || recipeOutputs.Length > 0
                ? (profile.ModelPrefabs ?? new List<GameObject>()).Select(AssetDatabase.GetAssetPath).Concat(recipeOutputs).Distinct(StringComparer.Ordinal).ToArray()
                : assets.Where(path => path.StartsWith(directory, StringComparison.Ordinal) && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)).ToArray();
            var ids = new List<string>();
            foreach (string path in paths)
            {
                string id = Path.GetFileNameWithoutExtension(path);
                if (!Token.IsMatch(id ?? "") || path != directory + id + ".prefab" || !assets.Contains(path))
                { errors.Add("模型成品必须位于标准角色目录，且使用合法人物 ID：" + path); continue; }
                if (ids.Contains(id)) errors.Add("模型清单重复：" + id); else ids.Add(id);
            }
            if (profile.Mode == ModBuildMode.Model && ids.Count == 0) errors.Add("模型模式至少需要一个完整人物 Prefab。");
            // SDK 与本体共用人物校验规则；导出器会一并提供该编辑器检查器。
            errors.AddRange(WBS.Client.Editor.BuildTools.CharacterBuildValidation.Check(paths));
            return ids.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        }

        internal static bool IsExportableAssembly(string name) => AssemblyToken.IsMatch(name ?? "") &&
            !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !ModSDKEnvironment.IsHostAssembly(name) &&
            !name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("System", StringComparison.OrdinalIgnoreCase) &&
            !name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) && !name.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) &&
            name != "netstandard" && !name.StartsWith("Assembly-CSharp", StringComparison.OrdinalIgnoreCase);

        internal static bool IsNumericVersion(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64) return false;
            string[] parts = value.Split('.');
            return parts.Length <= 4 && parts.All(part => part.Length > 0 && part.All(c => c >= '0' && c <= '9') && int.TryParse(part, out _));
        }

        public static string FullSourcePath(string path) => ModSDKEnvironment.GetPhysicalPath(path);

        private static void CopyAdditionalContent(BuildInputs input, string stage)
        {
            if (Directory.Exists(input.DataDirectory)) CopyTree(input.DataDirectory, Path.Combine(stage, "data"));
            if (input.IconPath != null) File.Copy(input.IconPath, Path.Combine(stage, input.Info.IconPath));
        }

        internal static void CopyTree(string source, string target)
        {
            EnsurePlainPath(source);
            Directory.CreateDirectory(target);
            foreach (string path in Directory.EnumerateFileSystemEntries(source))
            {
                EnsurePlainPath(path);
                string destination = Path.Combine(target, Path.GetFileName(path));
                if (Directory.Exists(path)) CopyTree(path, destination);
                else if (!new[] { ".meta", ".pdb", ".mdb" }.Contains(Path.GetExtension(path).ToLowerInvariant())) File.Copy(path, destination);
            }
        }
    }
}
