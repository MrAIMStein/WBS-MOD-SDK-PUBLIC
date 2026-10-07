using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;
using WBS.Client.Editor.CharacterAuthoring;

namespace WBS.Client.Editor.ModSDK
{
    [Serializable]
    public sealed class ModSDKAsset
    {
        public string sourcePath;
        public string packagePath;
        public string guid;
    }

    [Serializable]
    public sealed class ModSDKEditorAlias
    {
        public string sourceAlias;
        public string sourcePath;
    }

    [Serializable]
    public sealed class ModSDKManifest
    {
        public string sdkVersion = ModSDKEnvironment.SourceVersion;
        public string modApiVersion = ModSDKEnvironment.RequiredModApiVersion;
        public string gameVersion;
        // 来源信息供维护者追溯，不用于要求目标游戏版本或提交完全相同。
        public string sourceRevision, sourceFingerprint;
        public string unityVersion = ModSDKEnvironment.UnityVersion;
        public string addressablesVersion = ModSDKEnvironment.AddressablesVersion;
        public string urpVersion = ModSDKEnvironment.UrpVersion;
        public string inputSystemVersion = ModSDKEnvironment.InputSystemVersion;
        public string[] sharedAssetPaths = Array.Empty<string>();
        public string[] runtimeAssemblyNames = Array.Empty<string>();
        public ModSDKAsset[] assets = Array.Empty<ModSDKAsset>();
        public ModSDKEditorAlias[] editorAliases = Array.Empty<ModSDKEditorAlias>();
    }

    /// <summary>本体源码与独立作者包共用的资源定位契约，不依赖作者机器路径。</summary>
    public static class ModSDKEnvironment
    {
        public const string PackageName = "com.wbs.mod-sdk";
        public const string EditorPackageName = "com.wbs.mod-editor";
        public const string SourceVersion = "2.1.0";
        public const string RequiredModApiVersion = WBS.Client.Common.Mod.ModApiProtocol.CurrentVersion;
        public const string UnityVersion = "2022.3.62f3";
        public const string UnityRevision = "96770f904ca7";
        public const string AddressablesVersion = "1.22.3";
        public const string UrpVersion = "14.0.12";
        public const string InputSystemVersion = "1.14.0";
        public const string ManifestName = "sdk-manifest.json";
        private const string SourceDeclarationPath = "Assets/ModSDK/sdk-export.json";
        public const string DefaultTemplatePath = "Assets/Art/Prefabs/Character_Multi/CH1001.prefab";
        private static readonly string[] SharedRoots =
        {
            DefaultTemplatePath, "Assets/Art/Shaders/Toon.shader",
            "Assets/Art/Fonts/SourceHanSansSC-Regular SDF-Dynamic.asset",
            "Assets/Art/Fonts/SourceHanSans-LICENSE.txt",
            "Assets/Resources/ToonRenderSettings.asset", "Assets/Plugins/UGL/Shaders/Toon.shader",
            "Assets/Plugins/UGL/Shaders/Internal/Blit/Outline.shader",
            "Assets/Plugins/UGL/Shaders/Internal/Blit/Sobel.shader",
            "Assets/Plugins/UGL/Shaders/Internal/Blit/GaussianBlur.shader",
            "Assets/Plugins/UGL/Shaders/Internal/Blit/Scene Outline.shader",
            "Assets/Plugins/UGL/Shaders/Internal/Blit/Color Blend.shader"
        };
        private static readonly string[] ShaderIncludeDirectories =
        {
            "Assets/Art/Shaders/Includes/", "Assets/Plugins/UGL/Shaders/Includes/"
        };
        private static ModSDKManifest cachedSourceManifest, cachedInstalledManifest;
        private static string cachedManifestPath;
        private static string cachedSourceDeclarationJson;
        private static DateTime cachedManifestWriteTime;
        private static long cachedManifestLength;
        private static string[] cachedHostAssemblyNames;

        static ModSDKEnvironment()
        {
            EditorApplication.projectChanged += ClearCaches;
            CompilationPipeline.compilationFinished += _ => ClearCaches();
        }

        private static void ClearCaches()
        {
            cachedSourceManifest = cachedInstalledManifest = null;
            cachedHostAssemblyNames = null;
            cachedManifestPath = null;
            cachedSourceDeclarationJson = null;
        }

        public static string PackageRoot
        {
            get
            {
                string conventional = "Packages/" + PackageName;
                var info = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()
                    .FirstOrDefault(package => package.name == PackageName);
                // 包已经注册就必须按 SDK 处理；清单损坏不能悄悄退回本体资源路径。
                if (info != null) return conventional;
                return string.Empty;
            }
        }

        public static string InstalledVersion => ReadManifest().sdkVersion;
        public static string InstalledModApiVersion => ReadManifest().modApiVersion;

        /// <summary>只定位编辑器资源；纯工具包不会把本体识别为安装了宿主 DLL 的作者工程。</summary>
        public static string EditorToolsRoot
        {
            get
            {
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                bool hasSdk = packages.Any(package => package.name == PackageName);
                bool hasTools = packages.Any(package => package.name == EditorPackageName);
                if (hasSdk && hasTools)
                    throw new InvalidDataException("完整模组 SDK 与本体编辑器工具不能同时安装，请移除重复包。");
                if (hasSdk) return "Packages/" + PackageName;
                return hasTools ? "Packages/" + EditorPackageName : string.Empty;
            }
        }

        /// <summary>将共用工具的新源码地址和历史逻辑地址映射到 SDK 冻结快照。</summary>
        public static string ResolveEditorAssetPath(string originalPath)
        {
            if (string.IsNullOrEmpty(originalPath)) return originalPath;
            string root = EditorToolsRoot;
            if (!string.IsNullOrEmpty(PackageRoot))
            {
                ModSDKManifest manifest = ReadManifest();
                ModSDKEditorAlias alias = manifest.editorAliases.FirstOrDefault(item => item.sourceAlias == originalPath);
                if (alias != null) return ResolveAssetPath(alias.sourcePath);
                // 旧清单直接使用原逻辑地址；已有身份无需为路径迁移重新登记。
                if (manifest.assets.Any(item => item.sourcePath == originalPath)) return ResolveAssetPath(originalPath);
                return ResolveAssetPath(CharacterAuthoringContext.GetSourceEditorAssetPath(originalPath));
            }
            string sourcePath = CharacterAuthoringContext.GetSourceEditorAssetPath(originalPath);
            if (string.IsNullOrEmpty(root)) return sourcePath;
            string relative = sourcePath.Substring(CharacterAuthoringContext.SourceRoot.Length + 1);
            string resolved = root + "/Editor/SharedTools/" + relative;
            if (!File.Exists(GetPhysicalPath(resolved)))
                throw new InvalidDataException("模组工具的编辑器资源缺失，请重新安装工具包：" + resolved);
            return resolved;
        }

        public static ModSDKManifest ReadManifest()
        {
            string root = PackageRoot;
            if (!string.IsNullOrEmpty(root))
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(root + "/package.json");
                if (info == null || string.IsNullOrEmpty(info.resolvedPath))
                    throw new InvalidDataException("无法定位已注册的 SDK，请重新安装开发包。");
                string file = GetPhysicalPath(root + "/" + ManifestName);
                ModSDKManifest manifest = ReadInstalledManifest(file, info.version);
                // 缓存只省略 JSON 解析。包注册状态和资源文件可能独立变化，不能跳过校验。
                ValidateInstalledToolchain();
                ValidateInstalledAssetPaths(manifest, root);
                return manifest;
            }
            if (!File.Exists(SourceDeclarationPath)) throw new InvalidDataException("本体缺少游戏开发资源声明：" + SourceDeclarationPath);
            string declarationJson = File.ReadAllText(SourceDeclarationPath);
            ModSDKManifest declaration = ReadSourceDeclaration(declarationJson);
            ValidateInstalledToolchain();
            if (cachedSourceManifest != null && cachedSourceDeclarationJson == declarationJson)
                return cachedSourceManifest;
            string[] paths = AssetDatabase.GetDependencies(SharedRoots.Where(File.Exists).ToArray(), true)
                // include 和 UsePass 并非可靠的 AssetDatabase 依赖，显式补齐发布契约。
                .Concat(AssetDatabase.GetAllAssetPaths().Where(path =>
                    ShaderIncludeDirectories.Any(directory => path.StartsWith(directory, StringComparison.Ordinal)) &&
                    (path.EndsWith(".hlsl", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cginc", StringComparison.OrdinalIgnoreCase))))
                .Where(path => path.StartsWith("Assets/", StringComparison.Ordinal)).Distinct().OrderBy(path => path, StringComparer.Ordinal).ToArray();
            cachedSourceManifest = new ModSDKManifest
            {
                sdkVersion = declaration.sdkVersion, modApiVersion = declaration.modApiVersion,
                gameVersion = declaration.gameVersion, unityVersion = declaration.unityVersion,
                sourceRevision = declaration.sourceRevision, sourceFingerprint = declaration.sourceFingerprint,
                addressablesVersion = declaration.addressablesVersion, urpVersion = declaration.urpVersion,
                inputSystemVersion = declaration.inputSystemVersion,
                sharedAssetPaths = paths,
                runtimeAssemblyNames = GetHostRuntimeAssemblyNames(),
                assets = paths.Select(path => new ModSDKAsset
                {
                    sourcePath = path, packagePath = "Shared/" + path,
                    guid = AssetDatabase.AssetPathToGUID(path)
                }).ToArray()
            };
            cachedSourceDeclarationJson = declarationJson;
            return cachedSourceManifest;
        }

        private static ModSDKManifest ReadInstalledManifest(string file, string packageVersion)
        {
            file = Path.GetFullPath(file);
            var fileInfo = new FileInfo(file);
            if (!fileInfo.Exists) throw new InvalidDataException("SDK 缺少 " + ManifestName + "，请重新安装开发包。");
            ModSDKManifest manifest = cachedInstalledManifest;
            if (manifest == null || cachedManifestPath != file || cachedManifestWriteTime != fileInfo.LastWriteTimeUtc ||
                cachedManifestLength != fileInfo.Length)
            {
                manifest = new ModSDKManifest
                {
                    sdkVersion = null, modApiVersion = null, gameVersion = null, unityVersion = null,
                    addressablesVersion = null, urpVersion = null, inputSystemVersion = null,
                    assets = null, sharedAssetPaths = null, runtimeAssemblyNames = null
                };
                try { JsonUtility.FromJsonOverwrite(File.ReadAllText(file), manifest); }
                catch (ArgumentException error) { throw new InvalidDataException("SDK 清单 JSON 损坏，请重新安装开发包。", error); }
            }
            ValidateManifestMetadata(manifest, packageVersion);
            cachedInstalledManifest = manifest;
            cachedManifestPath = file;
            cachedManifestWriteTime = fileInfo.LastWriteTimeUtc;
            cachedManifestLength = fileInfo.Length;
            return manifest;
        }

        private static void ValidateInstalledAssetPaths(ModSDKManifest manifest, string root)
        {
            foreach (ModSDKAsset entry in manifest.assets)
            {
                string virtualPath = root + "/" + entry.packagePath;
                if (!File.Exists(GetPhysicalPath(virtualPath)))
                    throw new InvalidDataException("SDK 共享资源缺失，请重新安装开发包：" + virtualPath);
                if (!string.Equals(AssetDatabase.AssetPathToGUID(virtualPath), entry.guid, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SDK 共享资源 GUID 与清单不一致，请重新安装开发包：" + virtualPath);
            }
        }

        // 该 DTO 随 SDK 导出，不依赖仅供本体使用的 Exporter 类型。缺字段不能沿用 DTO 默认值。
        internal static ModSDKManifest ReadSourceDeclaration(string json)
        {
            var declaration = new ModSDKManifest
            {
                sdkVersion = null, modApiVersion = null, gameVersion = null, unityVersion = null,
                addressablesVersion = null, urpVersion = null, inputSystemVersion = null
            };
            try { JsonUtility.FromJsonOverwrite(json, declaration); }
            catch (ArgumentException error) { throw new InvalidDataException("游戏开发资源声明 JSON 无效。", error); }
            // 游戏资源声明不负责 SDK 包号；制作工具始终使用自身维护的版本。
            declaration.sdkVersion = SourceVersion;
            if (declaration.modApiVersion != RequiredModApiVersion ||
                declaration.unityVersion != UnityVersion || declaration.unityVersion != Application.unityVersion ||
                declaration.addressablesVersion != AddressablesVersion || declaration.urpVersion != UrpVersion ||
                declaration.inputSystemVersion != InputSystemVersion)
                throw new InvalidDataException("游戏开发资源声明的协议或工具链与当前源码不一致。");
            declaration.gameVersion = NormalizeSourceGameVersion(declaration.gameVersion);
            return declaration;
        }

        private static void ValidateManifestMetadata(ModSDKManifest manifest, string packageVersion)
        {
            if (manifest == null || manifest.assets == null || manifest.sharedAssetPaths == null ||
                manifest.runtimeAssemblyNames == null || manifest.runtimeAssemblyNames.Length == 0 || manifest.assets.Length == 0 ||
                !Regex.IsMatch(manifest.sdkVersion ?? string.Empty, @"^\d+\.\d+\.\d+$") ||
                manifest.sdkVersion != packageVersion)
                throw new InvalidDataException("SDK 清单缺失、损坏或版本与 package.json 不一致，请重新安装开发包。");
            manifest.gameVersion = NormalizeSourceGameVersion(manifest.gameVersion);
            if (manifest.modApiVersion != RequiredModApiVersion)
                throw new InvalidDataException("SDK 清单的模组接口协议不匹配，工具要求 " + RequiredModApiVersion +
                    "，清单为 " + (manifest.modApiVersion ?? "未声明") + "。请安装匹配的完整 SDK。");
            if (manifest.unityVersion != UnityVersion || manifest.unityVersion != Application.unityVersion ||
                manifest.addressablesVersion != AddressablesVersion || manifest.urpVersion != UrpVersion ||
                manifest.inputSystemVersion != InputSystemVersion)
                throw new InvalidDataException("SDK 清单的 Unity、Addressables、URP 或 InputSystem 版本与当前工具链不一致。");
            var sourcePaths = new HashSet<string>(StringComparer.Ordinal);
            var sharedPaths = new HashSet<string>(StringComparer.Ordinal);
            var packagePaths = new HashSet<string>(StringComparer.Ordinal);
            var guids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModSDKAsset entry in manifest.assets)
            {
                if (entry == null || !IsManifestAssetPath(entry.sourcePath, "Assets/") ||
                    (!IsManifestAssetPath(entry.packagePath, "Shared/") && !IsManifestAssetPath(entry.packagePath, "Editor/")) ||
                    !Regex.IsMatch(entry.guid ?? string.Empty, "^[0-9a-fA-F]{32}$") ||
                    !sourcePaths.Add(entry.sourcePath) || !packagePaths.Add(entry.packagePath) || !guids.Add(entry.guid))
                    throw new InvalidDataException("SDK 清单存在无效、重复的共享资源路径或 GUID。");
                if (entry.packagePath.StartsWith("Shared/", StringComparison.Ordinal)) sharedPaths.Add(entry.sourcePath);
            }
            if (!sharedPaths.SetEquals(manifest.sharedAssetPaths) || manifest.sharedAssetPaths.Length != sharedPaths.Count ||
                manifest.runtimeAssemblyNames.Any(string.IsNullOrWhiteSpace) ||
                manifest.runtimeAssemblyNames.Any(IsEditorAssemblyName) ||
                manifest.runtimeAssemblyNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != manifest.runtimeAssemblyNames.Length)
                throw new InvalidDataException("SDK 清单的共享资源集或运行时程序集集不完整。");
            if (manifest.editorAliases == null)
                throw new InvalidDataException("SDK 清单的编辑器别名表无效。");
            var aliases = new HashSet<string>(StringComparer.Ordinal);
            foreach (ModSDKEditorAlias alias in manifest.editorAliases)
                if (alias == null || !IsManifestAssetPath(alias.sourceAlias, "Assets/") ||
                    !IsManifestAssetPath(alias.sourcePath, CharacterAuthoringContext.SourceRoot + "/") ||
                    !aliases.Add(alias.sourceAlias) || sourcePaths.Contains(alias.sourceAlias) ||
                    !manifest.assets.Any(entry => entry.sourcePath == alias.sourcePath &&
                        entry.packagePath.StartsWith("Editor/SharedTools/", StringComparison.Ordinal)))
                    throw new InvalidDataException("SDK 编辑器别名重复、覆盖真实地址或指向未声明的共用工具。");
        }

        private static string NormalizeSourceGameVersion(string version)
        {
            version = version?.Trim();
            if (string.IsNullOrEmpty(version)) return null;
            if (!WBS.Client.Common.Build.WBSBuildRuntime.IsGameVersionValid(version))
                throw new InvalidDataException("SDK 内容来源 gameVersion 必须为空或三段 0 至 65535 的数字。");
            return version;
        }

        private static bool IsManifestAssetPath(string path, string prefix) => !string.IsNullOrEmpty(path) &&
            path.StartsWith(prefix, StringComparison.Ordinal) && !path.Contains('\\') && !path.Contains(':') &&
            path.Split('/').All(segment => segment.Length > 0 && segment != "." && segment != "..");

        private static void ValidateInstalledPackageVersion(string name, string expected)
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath("Packages/" + name + "/package.json");
            if (package == null || package.version != expected)
                throw new InvalidDataException("SDK 工具链版本不匹配，请安装 " + name + " " + expected + "。当前版本：" + (package?.version ?? "未安装"));
        }

        public static void ValidateInstalledToolchain()
        {
            // 相同程序集与资源 GUID 只能由一个包装提供。
            _ = EditorToolsRoot;
            ValidateInstalledPackageVersion("com.unity.addressables", AddressablesVersion);
            ValidateInstalledPackageVersion("com.unity.render-pipelines.universal", UrpVersion);
            ValidateInstalledPackageVersion("com.unity.inputsystem", InputSystemVersion);
        }

        public static string ResolveAssetPath(string originalPath)
        {
            if (string.IsNullOrEmpty(originalPath)) return originalPath;
            string root = PackageRoot;
            if (string.IsNullOrEmpty(root)) return originalPath;
            ModSDKManifest manifest = ReadManifest();
            string sourcePath = manifest.editorAliases.FirstOrDefault(item => item.sourceAlias == originalPath)?.sourcePath ?? originalPath;
            ModSDKAsset entry = manifest.assets.FirstOrDefault(item => item.sourcePath == sourcePath);
            if (entry == null) throw new InvalidDataException("SDK 清单未包含所需资源，请重新安装匹配的开发包：" + originalPath);
            return root + "/" + entry.packagePath;
        }

        /// <summary>将 AssetDatabase 的包虚拟路径转换为已注册包的真实文件路径。</summary>
        public static string GetPhysicalPath(string assetPath)
        {
            if (string.IsNullOrWhiteSpace(assetPath)) throw new ArgumentException("资源文件路径不能为空。", nameof(assetPath));
            string normalized = assetPath.Replace('\\', '/');
            if (!normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                string projectRoot = Directory.GetParent(Application.dataPath).FullName;
                return Path.GetFullPath(Path.IsPathRooted(assetPath) ? assetPath : Path.Combine(projectRoot, assetPath));
            }
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(normalized);
            if (package == null || string.IsNullOrEmpty(package.resolvedPath))
                throw new InvalidDataException("无法定位已注册包的物理目录：" + assetPath);
            string prefix = "Packages/" + package.name;
            if (normalized != prefix && !normalized.StartsWith(prefix + "/", StringComparison.Ordinal))
                throw new InvalidDataException("包虚拟路径与注册包身份不一致：" + assetPath);
            string root = Path.GetFullPath(package.resolvedPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string suffix = normalized == prefix ? string.Empty : normalized.Substring(prefix.Length + 1);
            string physical = Path.GetFullPath(Path.Combine(root, suffix));
            if (physical != root && !physical.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("包虚拟路径超出已注册包目录：" + assetPath);
            return physical;
        }

        public static bool IsSharedAsset(string path)
        {
            ModSDKManifest manifest = ReadManifest();
            string root = PackageRoot;
            // 源工程的宿主组件也能被作者 Prefab 使用，发布时仍须由导出器找到真实 DLL MonoScript。
            // 不把宿主程序集中的美术或 Editor 资源一并放开。
            if (string.IsNullOrEmpty(root) && !string.IsNullOrEmpty(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                return IsSourceHostRuntimeScript(path);
            return IsDeclaredSdkAssemblyAsset(path, root, manifest.runtimeAssemblyNames) ||
                manifest.sharedAssetPaths.Contains(path) || (!string.IsNullOrEmpty(root) &&
                manifest.assets.Any(item => item.packagePath.StartsWith("Shared/", StringComparison.Ordinal) && root + "/" + item.packagePath == path));
        }

        internal static bool IsSourceHostRuntimeScript(string path)
        {
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal) ||
                !path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || IsAuthorAssetPath(path) ||
                path.Contains('\\') || path.Split('/').Any(part => part.Length == 0 || part == "." || part == "..")) return false;
            Type type = AssetDatabase.LoadAssetAtPath<MonoScript>(path)?.GetClass();
            if (type == null || IsEditorAssemblyName(type.Assembly.GetName().Name)) return false;
            string full = GetPhysicalPath(path);
            return CompilationPipeline.GetAssemblies(AssembliesType.Player).Any(assembly =>
                string.Equals(assembly.name, type.Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase) &&
                !IsEditorAssemblyName(assembly.name) && !assembly.sourceFiles.All(IsAuthorAssetPath) &&
                assembly.sourceFiles.Any(source => string.Equals(GetPhysicalPath(source), full, StringComparison.OrdinalIgnoreCase)));
        }

        // Prefab 的 m_Script 在作者工程指向包内 DLL；这些宿主类型属于 SDK 依赖，不随模组再次导出。
        internal static bool IsDeclaredSdkAssemblyAsset(string path, string packageRoot, string[] runtimeNames) =>
            !string.IsNullOrEmpty(packageRoot) && !string.IsNullOrEmpty(path) &&
            path.EndsWith(".dll", StringComparison.Ordinal) &&
            path == packageRoot + "/Runtime/Assemblies/" + Path.GetFileName(path) &&
            (runtimeNames ?? Array.Empty<string>()).Contains(Path.GetFileNameWithoutExtension(path), StringComparer.Ordinal);

        public static bool IsHostAssembly(string name)
        {
            name = Path.GetFileName(name ?? string.Empty);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            if (IsEditorAssemblyName(name)) return false;
            return name.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) || name.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(name, "mscorlib", StringComparison.OrdinalIgnoreCase) || string.Equals(name, "netstandard", StringComparison.OrdinalIgnoreCase) ||
                ReadManifest().runtimeAssemblyNames.Contains(name, StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>完整 Player 宿主集供本体打包和 SDK 导出共用，作者自己的程序集不会被当成宿主。</summary>
        public static string[] GetHostRuntimeAssemblyNames()
        {
            if (!string.IsNullOrEmpty(PackageRoot)) return ReadManifest().runtimeAssemblyNames;
            if (cachedHostAssemblyNames != null) return cachedHostAssemblyNames;
            var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var playerAssemblies = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            var authorNames = new HashSet<string>(playerAssemblies.Where(assembly => assembly.sourceFiles.Length > 0 &&
                assembly.sourceFiles.All(IsAuthorAssetPath)).Select(assembly => assembly.name), StringComparer.OrdinalIgnoreCase);
            foreach (var assembly in playerAssemblies)
            {
                if (authorNames.Contains(assembly.name) || IsEditorAssemblyName(assembly.name)) continue;
                names.Add(assembly.name);
                foreach (string path in assembly.compiledAssemblyReferences)
                {
                    if (IsAuthorAssetPath(path)) continue;
                    string name;
                    try { name = System.Reflection.AssemblyName.GetAssemblyName(path).Name; }
                    catch (BadImageFormatException) { continue; }
                    catch (FileNotFoundException) { continue; }
                    if (!authorNames.Contains(name) && !IsEditorAssemblyName(name)) names.Add(name);
                }
            }
            // 还包括可进入 Windows Player、但没有直接源码引用的第三方插件。
            foreach (string path in AssetDatabase.GetAllAssetPaths().Where(path =>
                path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !IsAuthorAssetPath(path)))
            {
                if (!(AssetImporter.GetAtPath(path) is PluginImporter importer) ||
                    !IsWindowsPlayerPlugin(importer)) continue;
                string physical = GetPhysicalPath(path);
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic))
                {
                    string name = assembly.GetName().Name;
                    if (names.Contains(name) || authorNames.Contains(name) || IsEditorAssemblyName(name)) continue;
                    if (string.Equals(assembly.Location, physical, StringComparison.OrdinalIgnoreCase) &&
                        !assembly.GetReferencedAssemblies().Any(reference => IsEditorAssemblyName(reference.Name))) names.Add(name);
                }
            }
            cachedHostAssemblyNames = names.ToArray();
            return cachedHostAssemblyNames;
        }

        public static bool IsWindowsPlayerPlugin(PluginImporter importer) => importer.GetCompatibleWithAnyPlatform()
            ? !importer.GetExcludeFromAnyPlatform(BuildTarget.StandaloneWindows64)
            : importer.GetCompatibleWithPlatform(BuildTarget.StandaloneWindows64);

        private static bool IsEditorAssemblyName(string name) => name.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".Editor", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".CodeGen", StringComparison.OrdinalIgnoreCase) || name.StartsWith("Mono.Cecil", StringComparison.OrdinalIgnoreCase);

        private static bool IsAuthorAssetPath(string path)
        {
            string normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Assets/Mod/", StringComparison.OrdinalIgnoreCase)) return true;
            if (!Path.IsPathRooted(path)) return false;
            string root = Application.dataPath.Replace('\\', '/') + "/Mod/";
            return normalized.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }

        internal static System.Reflection.Assembly[] RuntimeAssemblies() => AppDomain.CurrentDomain.GetAssemblies()
            .Where(assembly => !assembly.IsDynamic &&
                (assembly.GetName().Name.StartsWith("WBS.", StringComparison.Ordinal) ||
                 assembly.GetName().Name.StartsWith("UGL.", StringComparison.Ordinal)) &&
                !assembly.GetName().Name.Contains("Editor") && !assembly.GetName().Name.Contains("Tests"))
            .OrderBy(assembly => assembly.GetName().Name, StringComparer.Ordinal).ToArray();
    }

    /// <summary>作者工程只适配共享人物工具的资源入口，不拥有共享工具源码。</summary>
    [InitializeOnLoad]
    internal static class ModSDKCharacterAuthoringContextBootstrap
    {
        static ModSDKCharacterAuthoringContextBootstrap()
            => CharacterAuthoringContext.SetContext(new SdkContext());

        private sealed class SdkContext : ICharacterAuthoringContext
        {
            public string TemplateVersion => ModSDKEnvironment.InstalledVersion;
            public string ResolveAssetPath(string originalPath) => ModSDKEnvironment.ResolveAssetPath(originalPath);
            public string ResolveEditorAssetPath(string originalPath) => ModSDKEnvironment.ResolveEditorAssetPath(originalPath);
        }
    }
}
