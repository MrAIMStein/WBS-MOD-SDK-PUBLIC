using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEditor.Compilation;
using UnityEngine;
using WBS.Client.Common.Mod;
using WBS.Client.Logic.Gameplay;
using AssemblyFlags = UnityEditor.Compilation.AssemblyFlags;
using PlayerAssembly = UnityEditor.Compilation.Assembly;

namespace WBS.Client.Editor.ModSDK
{
    public static partial class ModBuildService
    {
        private static void ValidateDependencyAssemblies(BuildInputs input, List<string> errors)
        {
            var exported = new HashSet<string>(input.AuxiliaryAssemblies.Concat(input.ExternalDlls.Select(Path.GetFileNameWithoutExtension)), StringComparer.OrdinalIgnoreCase)
            { input.Info.KeyName + "ModEntry" };
            string[] dependencies = AssetDatabase.GetDependencies(input.Assets, true);
            var editorAssemblies = new HashSet<string>(CompilationPipeline.GetAssemblies(AssembliesType.Editor)
                .Where(assembly => (assembly.flags & AssemblyFlags.EditorAssembly) != 0).Select(assembly => assembly.name), StringComparer.OrdinalIgnoreCase);
            var componentScripts = new HashSet<string>(StringComparer.Ordinal);
            foreach (string prefabPath in dependencies.Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                if (prefab == null) continue;
                foreach (MonoBehaviour component in prefab.GetComponentsInChildren<MonoBehaviour>(true))
                {
                    if (component == null) continue;
                    MonoScript script = MonoScript.FromMonoBehaviour(component);
                    if (script != null) componentScripts.Add(AssetDatabase.GetAssetPath(script));
                }
            }
            foreach (string path in dependencies)
            {
                bool source = path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
                if (!source && !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                string name = source ? CompilationPipeline.GetAssemblyNameFromScriptPath(path) : ReadDllMetadata(FullSourcePath(path)).Name;
                if (name != null && name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = Path.GetFileNameWithoutExtension(name);
                if (string.IsNullOrEmpty(name)) { errors.Add("无法确定资源依赖的程序集：" + path); continue; }
                bool editorAssembly = editorAssemblies.Contains(name) || name.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".Editor", StringComparison.OrdinalIgnoreCase) || name.Contains(".Editor.");
                if (IsOfficialEditorImportDependency(path, editorAssembly, componentScripts.Contains(path))) continue;
                if (editorAssembly)
                { errors.Add("资源依赖了编辑器组件或程序集，不能随模组分发：" + name + "（" + path + "）"); continue; }
                if (IsRuntimeReference(name)) continue;
                if (source && input.Profile.Mode == ModBuildMode.Model)
                { errors.Add("模型资源含有自定义源码组件，请改用自定义入口模式并显式导出其程序集：" + path); continue; }
                if (!exported.Contains(name)) errors.Add("资源依赖的非宿主程序集未列入导出名单：" + name + "（" + path + "）");
            }
        }

        internal static bool IsOfficialEditorImportDependency(string path, bool editorAssembly, bool componentReference) =>
            editorAssembly && !componentReference && path.StartsWith("Packages/com.unity.", StringComparison.Ordinal) &&
            path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);

        private static void ValidateCustomAssemblyOwnership(BuildInputs input, List<string> errors)
        {
            PlayerAssembly[] assemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            foreach (string name in new[] { input.Info.KeyName + "ModEntry" }.Concat(input.AuxiliaryAssemblies))
            {
                PlayerAssembly assembly = assemblies.SingleOrDefault(candidate => candidate.name == name);
                if (assembly == null) { errors.Add("未找到本模组 Player 程序集，请创建名称正确的 asmdef：" + name); continue; }
                if (assembly.sourceFiles == null || assembly.sourceFiles.Length == 0 ||
                    assembly.sourceFiles.Any(file => !IsWithin(FullSourcePath(file), FullSourcePath(input.SourceRoot))))
                    errors.Add("程序集混入资源根外的代码，禁止导出：" + name);
                if ((assembly.flags & AssemblyFlags.EditorAssembly) != 0) errors.Add("不能导出编辑器程序集：" + name);
            }
        }

        private static async Task<string[]> BuildAssemblies(BuildInputs input, string work, string stage, ModBuildReport report)
        {
            var exported = new List<string>();
            string entryName = input.Info.KeyName + "ModEntry";
            string entry = Path.Combine(stage, entryName + ".dll");
            if (input.Profile.Mode == ModBuildMode.Model)
            {
                string codePath = Path.Combine(work, "ModelModEntry.cs");
                File.WriteAllText(codePath, CreateModelEntrySource(input.Info, input.ModelIds), Utf8);
                string compiledEntry = Path.Combine(work, entryName + ".dll");
                var completion = new TaskCompletionSource<bool>();
                var builder = new AssemblyBuilder(compiledEntry, new[] { codePath })
                {
                    buildTarget = BuildTarget.StandaloneWindows64,
                    buildTargetGroup = BuildTargetGroup.Standalone,
                    flags = input.Profile.DebugBuild ? AssemblyBuilderFlags.DevelopmentBuild : AssemblyBuilderFlags.None,
                    additionalReferences = ModelEntryReferences(),
                    additionalDefines = new[] { "WBS_MOD_BUILD" }
                };
                ConfigureModelCompilerReferences(builder);
                WriteModelCompilerReferences(builder);
                builder.buildFinished += (_, messages) =>
                {
                    CompilerMessage[] failures = messages.Where(message => message.type == CompilerMessageType.Error).ToArray();
                    if (failures.Length > 0) completion.TrySetException(new InvalidDataException("模型入口编译失败：\n" +
                        string.Join("\n", failures.Select(message => message.message))));
                    else completion.TrySetResult(true);
                };
                if (!builder.Build()) throw new InvalidOperationException("无法启动模型入口编译。");
                await completion.Task;
                if (!File.Exists(compiledEntry)) throw new InvalidDataException("模型入口编译未产生 DLL。");
                File.Copy(compiledEntry, entry);
                if (input.Profile.DebugBuild) CopyDebugSymbols(compiledEntry, entry);
            }
            else
            {
                string compiled = Path.Combine(work, "player-scripts");
                Directory.CreateDirectory(compiled);
                var settings = new ScriptCompilationSettings
                {
                    group = BuildTargetGroup.Standalone, target = BuildTarget.StandaloneWindows64,
                    subtarget = (int)StandaloneBuildSubtarget.Player,
                    options = input.Profile.DebugBuild ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None,
                    extraScriptingDefines = new[] { "WBS_MOD_BUILD" }
                };
                ScriptCompilationResult result = PlayerBuildInterface.CompilePlayerScripts(settings, compiled);
                if (result.assemblies == null || result.assemblies.Count == 0) throw new InvalidDataException("Player 编译没有产生程序集。");
                var candidatesByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string name in new[] { entryName }.Concat(input.AuxiliaryAssemblies))
                {
                    string[] candidates = Directory.GetFiles(compiled, name + ".dll", SearchOption.AllDirectories);
                    if (candidates.Length != 1) throw new InvalidDataException("本次编译必须且只能产生一个 " + name + ".dll。");
                    candidatesByName.Add(name, candidates[0]);
                }
                report.PlayerWovenTypes = ValidateNetworkAssemblies(candidatesByName.Values.Concat(input.ExternalDlls).ToArray(),
                    Directory.GetFiles(compiled, "*.dll", SearchOption.AllDirectories).Concat(
                        GetNetworkReferencePaths(CompilationPipeline.GetAssemblies(AssembliesType.Player))).ToArray());
                foreach (string name in new[] { entryName }.Concat(input.AuxiliaryAssemblies))
                {
                    string target = name == entryName ? entry : Path.Combine(stage, "dll", name + ".dll");
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    File.Copy(candidatesByName[name], target);
                    if (input.Profile.DebugBuild) CopyDebugSymbols(candidatesByName[name], target);
                }
            }
            exported.Add(entry);
            foreach (string name in input.AuxiliaryAssemblies) exported.Add(Path.Combine(stage, "dll", name + ".dll"));
            foreach (string source in input.ExternalDlls)
            {
                string target = Path.Combine(stage, "dll", Path.GetFileName(source));
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                File.Copy(source, target);
                if (input.Profile.DebugBuild) CopyDebugSymbols(source, target);
                exported.Add(target);
            }
            ValidateDllSet(exported.ToArray(), input.Info.KeyName);
            report.Warnings = (report.Warnings ?? Array.Empty<string>())
                .Concat(GetHostContractWarnings(exported)).Distinct().ToArray();
            report.DebugSymbols = Directory.GetFiles(stage, "*", SearchOption.AllDirectories)
                .Where(file => file.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".mdb", StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetRelativePath(stage, file).Replace('\\', '/')).OrderBy(path => path, StringComparer.Ordinal).ToArray();
            return exported.Select(file => Path.GetRelativePath(stage, file).Replace('\\', '/')).ToArray();
        }

        internal static void CopyDebugSymbols(string sourceDll, string targetDll)
        {
            string source = Path.ChangeExtension(sourceDll, ".pdb");
            if (File.Exists(source)) File.Copy(source, Path.ChangeExtension(targetDll, ".pdb"));
            if (File.Exists(sourceDll + ".mdb")) File.Copy(sourceDll + ".mdb", targetDll + ".mdb");
        }

        /// <summary>AssemblyBuilder 默认可能引用旧合并 UnityEngine；它与现代模块重复定义属性类型。</summary>
        internal static void ConfigureModelCompilerReferences(AssemblyBuilder builder)
        {
            builder.referencesOptions = ReferencesOptions.UseEngineModules;
            builder.additionalReferences = builder.additionalReferences
                .Where(path => !IsMergedUnityEngineReference(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }

        internal static bool IsMergedUnityEngineReference(string path) =>
            string.Equals(Path.GetFileNameWithoutExtension(path), "UnityEngine", StringComparison.OrdinalIgnoreCase);

        [Serializable]
        private sealed class ModelCompilerReferences
        {
            public string[] DefaultReferences;
            public string[] AdditionalReferences;
            public string[] ExcludedReferences;
            public string CoreModulePath;
        }

        [MenuItem("WBS/模组/诊断模型入口编译引用")]
        public static void LogModelCompilerReferences()
        {
            var builder = new AssemblyBuilder(Path.Combine(ProjectRoot, "Library/ModBuild/Diagnostic/ModelModEntry.dll"),
                new[] { Path.Combine(ProjectRoot, "Library/ModBuild/Diagnostic/ModelModEntry.cs") })
            {
                buildTarget = BuildTarget.StandaloneWindows64, buildTargetGroup = BuildTargetGroup.Standalone,
                flags = AssemblyBuilderFlags.None,
                additionalReferences = ModelEntryReferences()
            };
            ConfigureModelCompilerReferences(builder);
            WriteModelCompilerReferences(builder);
            Debug.Log("模型入口实际编译引用已记录：Logs/ModSDK/model-compiler-references.json");
        }

        private static void WriteModelCompilerReferences(AssemblyBuilder builder)
        {
            string logs = Path.Combine(ProjectRoot, "Logs/ModSDK");
            EnsurePlainPath(logs);
            Directory.CreateDirectory(logs);
            File.WriteAllText(Path.Combine(logs, "model-compiler-references.json"), JsonUtility.ToJson(new ModelCompilerReferences
            {
                DefaultReferences = builder.defaultReferences, AdditionalReferences = builder.additionalReferences,
                ExcludedReferences = builder.excludeReferences, CoreModulePath = typeof(MonoBehaviour).Assembly.Location
            }, true) + "\n", Utf8);
        }

        internal static string CreateModelEntrySource(ModInfo info, string[] modelIds)
        {
            string registrations = string.Join("\n", modelIds.Select(id =>
                "            api.RegisterCharacter(new WBS.Client.Logic.ModSupport.ModCharacterDefinition { ModelId = \"" +
                id + "\", PrefabAddress = \"Art/Prefabs/Character_Multi/" + id + ".prefab\" });"));
            // ID 已在预检中限制为安全标识；入口不含实例化、扫描或资源加载代码。
            return "// 自动生成的模型模组入口，仅登记构建配置中的人物。\n" +
                "namespace WBS.Client.Mod.@" + info.KeyName + "\n{\n" +
                "    public sealed class " + info.KeyName + "Mod : WBS.Client.Common.Mod.ModBase\n    {\n" +
                "        public override bool OnLoad()\n        {\n" +
                "            var api = WBS.Client.Logic.ModSupport.ModApi.For(Context);\n" + registrations + "\n            return true;\n        }\n    }\n}\n";
        }

        private static string[] ModelEntryReferences() => new[]
        {
            typeof(ModBase).Assembly.Location, typeof(ModStaticDataManager).Assembly.Location,
            typeof(WBS.Client.Logic.ModSupport.ModApi).Assembly.Location, typeof(MonoBehaviour).Assembly.Location
        };

        internal static void ValidateDllSet(string[] files, string keyName)
        {
            var metadata = files.Select(ReadDllMetadata).ToArray();
            var byName = new Dictionary<string, DllMetadata>(StringComparer.OrdinalIgnoreCase);
            foreach (DllMetadata item in metadata)
            {
                if (Path.GetFileNameWithoutExtension(item.Path) != item.Name || !IsExportableAssembly(item.Name) || !byName.TryAdd(item.Name, item))
                    throw new InvalidDataException("DLL 身份不匹配、重复或属于宿主：" + item.Path);
            }
            string entryName = keyName + "ModEntry";
            string entryType = "WBS.Client.Mod." + keyName + "." + keyName + "Mod";
            if (!byName.TryGetValue(entryName, out DllMetadata entry) || !entry.Types.TryGetValue(entryType, out string baseType))
                throw new InvalidDataException("入口必须包含 WBS.Client.Mod." + keyName + "." + keyName + "Mod。");
            if (!entry.CreatableTypes.Contains(entryType)) throw new InvalidDataException("入口必须是公开的非抽象类，并具有公开无参数构造函数。");
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (baseType != "WBS.Client.Common.Mod.ModBase" && visited.Add(baseType ?? ""))
            {
                DllMetadata owner = metadata.FirstOrDefault(item => item.Types.ContainsKey(baseType ?? ""));
                if (owner == null || !owner.Types.TryGetValue(baseType ?? "", out baseType)) break;
            }
            if (baseType != "WBS.Client.Common.Mod.ModBase") throw new InvalidDataException("入口类型必须继承 ModBase。");
            foreach (DllMetadata item in metadata)
                foreach (string reference in item.References)
                    if (!byName.ContainsKey(reference) && !IsRuntimeReference(reference))
                        throw new InvalidDataException(item.Name + " 引用了未随包分发且不属于 SDK 的程序集：" + reference);
        }

        private static bool IsRuntimeReference(string name) => !name.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase) &&
            (ModSDKEnvironment.IsHostAssembly(name) || name == "mscorlib" || name == "netstandard" ||
             name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("UnityEngine", StringComparison.Ordinal));

        private sealed class DllMetadata
        {
            internal string Path, Name, HostContractInspectionError;
            internal string[] References;
            internal readonly Dictionary<string, string> Types = new(StringComparer.Ordinal);
            internal readonly HashSet<string> CreatableTypes = new(StringComparer.Ordinal);
            internal readonly SortedSet<string> NonStableHostReferences = new(StringComparer.Ordinal);
        }

        private sealed class StableHostContract
        {
            internal readonly HashSet<string> Types = new(StringComparer.Ordinal);
            internal readonly HashSet<string> Members = new(StringComparer.Ordinal);
        }

        private static readonly Lazy<StableHostContract> HostContract = new(CreateStableHostContract);

        /// <summary>仅展开约定的 API、DTO 与扩展点签名，不读取方法体或沿管理器实现继续扩散。</summary>
        private static StableHostContract CreateStableHostContract()
        {
            var contract = new StableHostContract();
            var roots = new[]
            {
                typeof(ModBase), typeof(ModContext), typeof(ModResources), typeof(ModAssetHandle<>), typeof(ModLog),
                typeof(ModAssemblyScope), typeof(ModContextState), typeof(WBS.Client.Logic.ModSupport.ModApi),
                typeof(WBS.Client.Logic.ModSupport.ModUiApi), typeof(WBS.Client.Logic.ModSupport.ModAuthorActions),
                typeof(WBS.Client.Logic.ModSupport.ModCharacterDefinition), typeof(WBS.Client.Logic.ModSupport.ModGameplayDefinition),
                typeof(WBS.Client.Logic.ModSupport.ModMapDefinition), typeof(WBS.Client.Logic.ModSupport.ModItemDefinition),
                typeof(WBS.Client.Logic.UI.ModUi.IModUiPanel), typeof(WBS.Client.Logic.UI.ModUi.ModUiSession),
                typeof(WBS.Client.Common.WBG.WBGInfo), typeof(WBS.Client.Common.WBG.WBGConfig),
                typeof(WBS.Client.Common.WBG.WBGMapMetadata), typeof(WBS.Client.Common.WBG.IRequiredPlayingPlayerCountSettings),
                typeof(WBS.Client.Common.WBG.IPlayerAssignmentSettings), typeof(WBS.Client.Common.WBG.IInitialCoinSettings),
                typeof(WBS.Client.Common.WBG.IWBGSettingsEditorPolicy), typeof(WBS.Client.Common.WBG.IWBGSettingsVisibilityPolicy),
                typeof(IWBGGameplayActionPolicy), typeof(IWBGReviewPhasePolicy),
                typeof(WBS.Client.Logic.Gameplay.Item.ItemInfo), typeof(WBS.Client.Logic.Gameplay.Item.ItemInfoSO),
                typeof(WBS.Client.Logic.Gameplay.Item.ItemAction), typeof(WBS.Client.Logic.Gameplay.Item.ItemPresentation),
                typeof(WBS.Client.Logic.Gameplay.Pad.IPadApp), typeof(WBS.Client.Logic.Gameplay.Pad.PadAppInfo),
                typeof(WBS.Client.Logic.Gameplay.Pad.PadAppInfoListSO),
                typeof(WBS.Client.Logic.Gameplay.Interact.IPlayerInteractionActionProvider),
                typeof(WBS.Client.Logic.Gameplay.Interact.PlayerInteractionAction),
                typeof(WBS.Client.Logic.Gameplay.Interact.PlayerInteractionContext)
            };
            // 扩展基类只承诺公开/受保护的虚方法、数据字段及下列明确辅助成员；普通内部调用仍提示。
            var extensions = new Dictionary<Type, HashSet<string>>
            {
                [typeof(WBGControllerBase)] = new(StringComparer.Ordinal),
                [typeof(WBS.Client.Common.WBG.WBGSettings)] = new(StringComparer.Ordinal),
                [typeof(WBS.Client.Logic.Gameplay.Item.ItemBehaviour)] = new(StringComparer.Ordinal)
                    { "get_Uid", "get_HasUid", "get_IsInHand" },
                [typeof(WBS.Client.Logic.Gameplay.Item.ItemDataModel)] = new(StringComparer.Ordinal)
                    { "ToPresentation", "CopyNameFormatArguments" },
                [typeof(WBS.Client.Logic.Gameplay.Map.WBGMapBase)] = new(StringComparer.Ordinal)
                    { "Load", "Unload", "get_ItemsBound" }
            };
            var pending = new Queue<Type>(roots.Concat(extensions.Keys));
            var expanded = new HashSet<Type>();
            while (pending.Count != 0)
            {
                Type type = pending.Dequeue();
                if (!expanded.Add(type)) continue;
                contract.Types.Add(HostTypeKey(type));
                foreach (Type implemented in type.GetInterfaces()) AddType(implemented);
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
                    BindingFlags.Static | BindingFlags.DeclaredOnly;
                foreach (FieldInfo field in type.GetFields(flags).Where(field => field.IsPublic || field.IsFamily || field.IsFamilyOrAssembly))
                {
                    contract.Members.Add(HostTypeKey(type) + "::F:" + field.Name + ":" + SignatureType(field.FieldType));
                    AddType(field.FieldType);
                }
                foreach (MethodBase method in type.GetMethods(flags).Cast<MethodBase>().Concat(type.GetConstructors(flags))
                    .Where(method => method.IsPublic || method.IsFamily || method.IsFamilyOrAssembly))
                {
                    if (extensions.TryGetValue(type, out HashSet<string> helpers) && method is MethodInfo extension &&
                        !extension.IsVirtual && !helpers.Contains(extension.Name)) continue;
                    contract.Members.Add(HostTypeKey(type) + "::" + MethodSignature(method));
                    if (method is MethodInfo info) AddType(info.ReturnType);
                    foreach (ParameterInfo parameter in method.GetParameters()) AddType(parameter.ParameterType);
                    foreach (Type parameter in MethodGenericArguments(method))
                        foreach (Type constraint in parameter.GetGenericParameterConstraints()) AddType(constraint);
                }
            }
            return contract;

            void AddType(Type type)
            {
                if (type.HasElementType) { AddType(type.GetElementType()); return; }
                if (type.IsGenericParameter) return;
                if (type.IsGenericType)
                {
                    foreach (Type argument in type.GetGenericArguments()) AddType(argument);
                    type = type.GetGenericTypeDefinition();
                }
                if (!IsHostContractType(type)) return;
                contract.Types.Add(HostTypeKey(type));
                // 返回值中的实现对象仅允许持有其类型；只有 DTO、枚举、接口签名继续展开。
                if (type.IsEnum || type.IsValueType || type.IsInterface ||
                    type.IsSerializable && !typeof(Component).IsAssignableFrom(type) && !typeof(ScriptableObject).IsAssignableFrom(type))
                    pending.Enqueue(type);
            }
        }

        private static bool IsHostContractType(Type type) => IsHostNamespace(type.FullName) &&
            ModSDKEnvironment.IsHostAssembly(type.Assembly.GetName().Name);

        private static bool IsHostNamespace(string name) => name != null &&
            !name.StartsWith("WBS.Client.Mod.", StringComparison.Ordinal) &&
            (name.StartsWith("WBS.", StringComparison.Ordinal) || name.StartsWith("UGL.", StringComparison.Ordinal));

        private static string HostTypeKey(Type type) => type.Assembly.GetName().Name + "::" + type.FullName.Replace('+', '/');

        private static string SignatureType(Type type)
        {
            if (type.IsGenericParameter) return (type.DeclaringMethod == null ? "!" : "!!") + type.GenericParameterPosition;
            if (type.IsByRef) return SignatureType(type.GetElementType()) + "&";
            if (type.IsPointer) return SignatureType(type.GetElementType()) + "*";
            if (type.IsArray) return SignatureType(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            if (type.IsGenericType) return type.GetGenericTypeDefinition().FullName.Replace('+', '/') + "<" +
                string.Join(",", type.GetGenericArguments().Select(SignatureType)) + ">";
            return type.FullName.Replace('+', '/');
        }

        private static Type[] MethodGenericArguments(MethodBase method) => method is MethodInfo info ? info.GetGenericArguments() : Type.EmptyTypes;

        private static string MethodSignature(MethodBase method) => "M:" + method.Name + "`" + MethodGenericArguments(method).Length +
            "(" + string.Join(",", method.GetParameters().Select(parameter => SignatureType(parameter.ParameterType))) + "):" +
            (method is MethodInfo info ? SignatureType(info.ReturnType) : "System.Void");

        private static string[] GetHostContractWarnings(IEnumerable<string> files)
        {
            var warnings = new List<string>();
            foreach (DllMetadata metadata in files.Distinct(StringComparer.OrdinalIgnoreCase).Select(ReadDllMetadata))
            {
                if (metadata.HostContractInspectionError != null)
                    warnings.Add("程序集 " + metadata.Name + " 的宿主稳定接口检查未完成，允许打包，请自行检查引用：" + metadata.HostContractInspectionError);
                if (metadata.NonStableHostReferences.Count != 0)
                    warnings.Add("程序集 " + metadata.Name + " 使用未承诺稳定的宿主接口：" + string.Join("；", metadata.NonStableHostReferences.OrderBy(value => value, StringComparer.Ordinal)) +
                        "。允许打包，但游戏更新后请自行验证。静态元数据检查无法覆盖反射、dynamic 或字符串加载。");
            }
            return warnings.Distinct().ToArray();
        }

        private static void ReadHostContractReferences(object module, DllMetadata result)
        {
            StableHostContract contract = HostContract.Value;
            foreach (object type in ((IEnumerable)module.GetType().GetMethod("GetTypeReferences", Type.EmptyTypes).Invoke(module, null)).Cast<object>())
                CheckType(type);
            foreach (object member in ((IEnumerable)module.GetType().GetMethod("GetMemberReferences", Type.EmptyTypes).Invoke(module, null)).Cast<object>())
            {
                object declaring = Property(member, "DeclaringType");
                CheckType(declaring);
                string key = HostReferenceKey(declaring);
                if (key == null) continue;
                object method = member;
                if (TryMetadataProperty(method, "ElementMethod", out object element)) method = element;
                string signature;
                if (TryMetadataProperty(method, "Parameters", out object parameters))
                {
                    object returned = Property(method, "ReturnType");
                    CheckType(returned);
                    object[] arguments = ((IEnumerable)parameters).Cast<object>().Select(parameter => Property(parameter, "ParameterType")).ToArray();
                    foreach (object argument in arguments) CheckType(argument);
                    signature = "M:" + Property(method, "Name") + "`" + (int)Property(Property(method, "GenericParameters"), "Count") +
                        "(" + string.Join(",", arguments.Select(MetadataSignatureType)) + "):" + MetadataSignatureType(returned);
                }
                else
                {
                    object field = Property(member, "FieldType");
                    CheckType(field);
                    signature = "F:" + Property(member, "Name") + ":" + MetadataSignatureType(field);
                }
                if (!contract.Members.Contains(key + "::" + signature))
                    result.NonStableHostReferences.Add(key.Substring(key.IndexOf("::", StringComparison.Ordinal) + 2) + "::" + signature);
            }

            void CheckType(object type)
            {
                if (type == null || (bool)Property(type, "IsGenericParameter")) return;
                if (TryMetadataProperty(type, "GenericArguments", out object arguments))
                    foreach (object argument in ((IEnumerable)arguments).Cast<object>()) CheckType(argument);
                if (TryMetadataProperty(type, "ElementType", out object element)) { CheckType(element); return; }
                string key = HostReferenceKey(type);
                if (key != null && !contract.Types.Contains(key))
                    result.NonStableHostReferences.Add((string)Property(type, "FullName"));
            }
        }

        private static string HostReferenceKey(object type)
        {
            if ((bool)Property(type, "IsGenericParameter")) return null;
            if (TryMetadataProperty(type, "ElementType", out object element)) return HostReferenceKey(element);
            string name = (string)Property(type, "FullName");
            if (!IsHostNamespace(name)) return null;
            object scope = Property(type, "Scope");
            if (scope == null) return null;
            string assembly = (string)Property(scope, "Name");
            // 作者自己的模块即使采用 WBS 命名空间也不属于宿主稳定性检查。
            if (scope.GetType().FullName != "Mono.Cecil.AssemblyNameReference" || !ModSDKEnvironment.IsHostAssembly(assembly)) return null;
            return assembly + "::" + name;
        }

        private static string MetadataSignatureType(object type)
        {
            if ((bool)Property(type, "IsGenericParameter"))
                return (Property(type, "Type").ToString() == "Method" ? "!!" : "!") + Property(type, "Position");
            if ((bool)Property(type, "IsByReference")) return MetadataSignatureType(Property(type, "ElementType")) + "&";
            if ((bool)Property(type, "IsPointer")) return MetadataSignatureType(Property(type, "ElementType")) + "*";
            if ((bool)Property(type, "IsArray")) return MetadataSignatureType(Property(type, "ElementType")) +
                "[" + new string(',', (int)Property(type, "Rank") - 1) + "]";
            if (TryMetadataProperty(type, "GenericArguments", out object arguments)) return
                MetadataSignatureType(Property(type, "ElementType")) + "<" +
                string.Join(",", ((IEnumerable)arguments).Cast<object>().Select(MetadataSignatureType)) + ">";
            if (TryMetadataProperty(type, "ElementType", out object element)) return MetadataSignatureType(element);
            return (string)Property(type, "FullName");
        }

        private static bool TryMetadataProperty(object value, string name, out object result)
        {
            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo property = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(candidate => candidate.Name == name && candidate.GetIndexParameters().Length == 0);
                if (property == null) continue;
                result = property.GetValue(value);
                return true;
            }
            result = null;
            return false;
        }

        private static DllMetadata ReadDllMetadata(string path)
        {
            // Harmony 也包含 Cecil 类型，因此显式选 Unity 的 Mono.Cecil；读取元数据不会执行作者代码。
            System.Reflection.Assembly cecil = System.Reflection.Assembly.Load("Mono.Cecil");
            Type readerType = cecil.GetType("Mono.Cecil.ReaderParameters", true);
            Type definitionType = cecil.GetType("Mono.Cecil.AssemblyDefinition", true);
            object parameters = Activator.CreateInstance(readerType);
            readerType.GetProperty("ReadSymbols").SetValue(parameters, false);
            object definition = definitionType.GetMethod("ReadAssembly", new[] { typeof(string), readerType })
                .Invoke(null, new[] { (object)path, parameters });
            try
            {
                object module = Property(definition, "MainModule");
                var result = new DllMetadata
                {
                    Path = path, Name = (string)Property(Property(definition, "Name"), "Name"),
                    References = ((IEnumerable)Property(module, "AssemblyReferences")).Cast<object>()
                        .Select(reference => (string)Property(reference, "Name")).ToArray()
                };
                foreach (object type in ((IEnumerable)Property(module, "Types")).Cast<object>())
                {
                    string typeName = (string)Property(type, "FullName");
                    result.Types[typeName] = Property(type, "BaseType") is object parent ? (string)Property(parent, "FullName") : null;
                    if (!(bool)Property(type, "IsPublic") || (bool)Property(type, "IsAbstract")) continue;
                    if (((IEnumerable)Property(type, "Methods")).Cast<object>().Any(method => (bool)Property(method, "IsConstructor") &&
                        !(bool)Property(method, "IsStatic") && (bool)Property(method, "IsPublic") && (int)Property(Property(method, "Parameters"), "Count") == 0))
                        result.CreatableTypes.Add(typeName);
                }
                if (IsExportableAssembly(result.Name))
                {
                    try { ReadHostContractReferences(module, result); }
                    catch (Exception exception) { result.HostContractInspectionError = exception.GetBaseException().Message; }
                }
                return result;
            }
            finally { (definition as IDisposable)?.Dispose(); }
        }

        private static object Property(object value, string name)
        {
            if (value == null) throw new InvalidDataException("程序集元数据对象为空，无法读取：" + name);
            // Cecil 的 MethodDefinition.DeclaringType 隐藏了返回 TypeReference 的基类属性。
            // 逐层查最派生声明，避免 GetProperty 合并不同返回类型后产生 AmbiguousMatch。
            for (Type type = value.GetType(); type != null; type = type.BaseType)
            {
                PropertyInfo[] matches = type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)
                    .Where(property => property.Name == name && property.GetIndexParameters().Length == 0).ToArray();
                if (matches.Length > 1) throw new InvalidDataException("程序集元数据属性声明不唯一：" + type.FullName + "." + name);
                if (matches.Length == 1) return matches[0].GetValue(value);
            }
            if (name == "BaseType") return null;
            throw new InvalidDataException("程序集元数据字段不存在：" + value.GetType().FullName + "." + name);
        }
    }
}
