using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Build.DataBuilders;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEditor.Build.Pipeline;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;
using UnityEngine.ResourceManagement.ResourceProviders;
using Object = UnityEngine.Object;

namespace WBS.Client.Editor.ModSDK
{
    public static partial class ModBuildService
    {
        private static void BuildResources(BuildInputs input, string work, string stage)
        {
            string bundleDirectory = Path.Combine(stage, "bundle");
            Directory.CreateDirectory(bundleDirectory);
            AddressableAssetSettings settings = null;
            BuildScriptPackedMode builder = null;
            string settingsRoot = "Assets/TempModBuild_" + Guid.NewGuid().ToString("N");
            using var protection = new ResourceBuildProtection();
            try
            {
                if (string.IsNullOrEmpty(AssetDatabase.CreateFolder("Assets", Path.GetFileName(settingsRoot))))
                    throw new IOException("无法创建独立构建配置目录。");
                // SBP 会卸载非持久对象。保存独立配置及组，避免重载后回退到本体默认设置。
                settings = ConfigureIsolatedSettings(AddressableAssetSettings.Create(settingsRoot, "ModSettings", false, true),
                    input.Info.UUID, input.Info.Version, bundleDirectory, work);
                foreach (string path in input.Assets)
                {
                    AddressableAssetEntry entry = settings.CreateOrMoveEntry(AssetDatabase.AssetPathToGUID(path), settings.DefaultGroup, false, false);
                    if (entry == null) throw new InvalidDataException("无法创建模组资源入口：" + path);
                    entry.address = path;
                }
                foreach (AddressableAssetGroup group in settings.groups.Where(group => group != null))
                {
                    foreach (AddressableAssetGroupSchema schema in group.Schemas) AssetDatabase.SaveAssetIfDirty(schema);
                    AssetDatabase.SaveAssetIfDirty(group);
                }
                AssetDatabase.SaveAssetIfDirty(settings);
                builder = ScriptableObject.CreateInstance<BuildScriptPackedMode>();
                string runtimePrefix = "wbs-mod-" + Guid.NewGuid().ToString("N");
                var context = new AddressablesDataBuilderInput(settings, input.Info.Version)
                {
                    // Addressables 对 settings 路径使用字符串拼接，因此必须传入唯一相对文件名。
                    RuntimeCatalogFilename = runtimePrefix + "-catalog.json",
                    RuntimeSettingsFilename = runtimePrefix + "-settings.json"
                };
                string catalogPath = protection.TrackRuntimeFile(context.RuntimeCatalogFilename);
                protection.TrackRuntimeFile(context.RuntimeSettingsFilename);
                protection.PrepareAddressablesPreferences();
                protection.RefreshShaderPrefilters();
                AddressablesPlayerBuildResult result = null;
                BuildWithoutCache(() => result = builder.BuildData<AddressablesPlayerBuildResult>(context));
                if (result == null || !string.IsNullOrEmpty(result.Error)) throw new InvalidOperationException("Addressables 构建失败：" + result?.Error);
                if (!File.Exists(catalogPath)) throw new InvalidDataException("构建未产生 JSON catalog。");
                File.Copy(catalogPath, Path.Combine(bundleDirectory, "catalog.json"));
            }
            finally
            {
                if (builder != null) Object.DestroyImmediate(builder);
                if (AssetDatabase.IsValidFolder(settingsRoot) && !AssetDatabase.DeleteAsset(settingsRoot))
                    throw new IOException("独立构建配置清理失败：" + settingsRoot);
            }
        }

        internal static AddressableAssetSettings CreateIsolatedSettings(string uuid, string version, string bundleDirectory, string work)
        {
            return ConfigureIsolatedSettings(AddressableAssetSettings.Create("", "WBS Mod " + uuid, false, false), uuid, version, bundleDirectory, work);
        }

        private static AddressableAssetSettings ConfigureIsolatedSettings(AddressableAssetSettings settings, string uuid, string version, string bundleDirectory, string work)
        {
            settings.BuildAddressablesWithPlayerBuild = AddressableAssetSettings.PlayerBuildOption.DoNotBuildWithPlayer;
            settings.BuildRemoteCatalog = false;
            settings.BundleLocalCatalog = false;
            settings.ContentStateBuildPath = Path.Combine(work, "content-state");
            settings.buildSettings.bundleBuildPath = Path.Combine(work, "bundle-work");
            settings.OverridePlayerVersion = version;
            string identity = "wbs-mod-" + Hash128.Compute(uuid).ToString();
            settings.ShaderBundleNaming = ShaderBundleNaming.Custom;
            settings.ShaderBundleCustomNaming = identity;
            settings.MonoScriptBundleNaming = MonoScriptBundleNaming.Custom;
            settings.MonoScriptBundleCustomNaming = identity;
            settings.UniqueBundleIds = true;
            settings.profileSettings.SetValue(settings.activeProfileId, AddressableAssetSettings.kLocalBuildPath, bundleDirectory.Replace('\\', '/'));
            settings.profileSettings.SetValue(settings.activeProfileId, AddressableAssetSettings.kLocalLoadPath, "CurrModBundleDir");
            AddressableAssetGroup group = settings.CreateGroup("Mod " + uuid, true, false, false, null,
                typeof(BundledAssetGroupSchema), typeof(ContentUpdateGroupSchema));
            BundledAssetGroupSchema schema = group.GetSchema<BundledAssetGroupSchema>();
            schema.BuildPath.SetVariableByName(settings, AddressableAssetSettings.kLocalBuildPath);
            schema.LoadPath.SetVariableByName(settings, AddressableAssetSettings.kLocalLoadPath);
            schema.BundleMode = BundledAssetGroupSchema.BundlePackingMode.PackTogether;
            schema.Compression = BundledAssetGroupSchema.BundleCompressionMode.LZ4;
            schema.IncludeAddressInCatalog = true;
            schema.IncludeGUIDInCatalog = false;
            schema.IncludeLabelsInCatalog = false;
            schema.InternalIdNamingMode = BundledAssetGroupSchema.AssetNamingMode.FullPath;
            schema.UseAssetBundleCache = false;
            schema.IncludeInBuild = true;
            if (settings.IsPersisted) AssetDatabase.SaveAssetIfDirty(settings);
            return settings;
        }

        internal static void BuildWithoutCache(Action build)
        {
            var callbacks = ContentPipeline.BuildCallbacks ?? throw new InvalidOperationException("SBP 构建回调不可用。");
            var previous = callbacks.PostScriptsCallbacks;
            callbacks.PostScriptsCallbacks = (parameters, results) =>
            {
                ReturnCode result = previous?.Invoke(parameters, results) ?? ReturnCode.Success;
                parameters.UseCache = false;
                return result;
            };
            try { build(); }
            finally { callbacks.PostScriptsCallbacks = previous; }
        }

        internal static void ValidatePublishedCatalog(string package, string[] expectedAddresses)
        {
            string root = Path.GetFullPath(Path.Combine(package, "bundle"));
            var catalog = JsonUtility.FromJson<ContentCatalogData>(File.ReadAllText(Path.Combine(root, "catalog.json"), Utf8));
            if (catalog == null) throw new InvalidDataException("catalog 格式无效。");
            var locator = catalog.CreateLocator();
            var visited = new HashSet<IResourceLocation>();
            int bundleCount = 0;
            foreach (object key in locator.Keys)
                if (locator.Locate(key, null, out IList<IResourceLocation> locations))
                    foreach (IResourceLocation location in locations) CheckLocation(location);
            foreach (string address in expectedAddresses ?? Array.Empty<string>())
                if (!locator.Locate(address, null, out IList<IResourceLocation> found) || found.Count == 0)
                    throw new InvalidDataException("catalog 缺少已选择资源：" + address);
            if (bundleCount == 0) throw new InvalidDataException("catalog 未包含任何完整的本地 bundle 依赖。");

            void CheckLocation(IResourceLocation location)
            {
                if (location == null || !visited.Add(location)) return;
                bool bundleProvider = location.ProviderId == typeof(AssetBundleProvider).FullName;
                if (bundleProvider && !(location.Data is AssetBundleRequestOptions))
                    throw new InvalidDataException("catalog 的 bundle 缺少构建元数据：" + location.InternalId);
                if (location.Data is AssetBundleRequestOptions)
                {
                    bundleCount++;
                    string path = location.InternalId.Replace("CurrModBundleDir", root);
                    if (Uri.TryCreate(path, UriKind.Absolute, out Uri uri))
                    {
                        if (!uri.IsFile) throw new InvalidDataException("发布包不得依赖在线 bundle：" + location.InternalId);
                        path = uri.LocalPath;
                    }
                    if (!Path.IsPathRooted(path)) path = Path.Combine(root, path);
                    path = Path.GetFullPath(path);
                    if (!IsWithin(path, root) || !File.Exists(path)) throw new InvalidDataException("catalog 依赖缺失或超出发布目录：" + location.InternalId);
                    EnsurePlainPath(path);
                }
                if (location.Dependencies != null) foreach (IResourceLocation dependency in location.Dependencies) CheckLocation(dependency);
            }
        }

        private static void RestoreAddressablesSettings(string path, string[] groupGuids, Hash128 hash, bool originallyDirty)
        {
            var settings = AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>(path);
            if (settings == null) throw new InvalidDataException("默认 Addressables 设置已丢失：" + path);
            var restoredGroups = new AddressableAssetGroup[groupGuids.Length];
            for (int i = 0; i < groupGuids.Length; i++)
            {
                if (string.IsNullOrEmpty(groupGuids[i])) continue;
                string groupPath = AssetDatabase.GUIDToAssetPath(groupGuids[i]);
                restoredGroups[i] = AssetDatabase.LoadAssetAtPath<AddressableAssetGroup>(groupPath);
                if (restoredGroups[i] == null) throw new InvalidDataException("默认 Addressables 组已丢失：" + groupGuids[i]);
            }
            using var serialized = new SerializedObject(settings);
            SerializedProperty groups = serialized.FindProperty("m_GroupAssets");
            SerializedProperty currentHash = serialized.FindProperty("m_currentHash");
            if (groups == null || !groups.isArray || currentHash == null || currentHash.propertyType != SerializedPropertyType.Hash128)
                throw new InvalidDataException("Addressables 1.21 的默认设置序列化结构已改变。");
            groups.arraySize = restoredGroups.Length;
            for (int i = 0; i < restoredGroups.Length; i++) groups.GetArrayElementAtIndex(i).objectReferenceValue = restoredGroups[i];
            currentHash.hash128Value = hash;
            if (serialized.ApplyModifiedPropertiesWithoutUndo()) EditorUtility.SetDirty(settings);
            if (originallyDirty) EditorUtility.SetDirty(settings);
            else AssetDatabase.SaveAssetIfDirty(settings);
        }

        /// <summary>保存持久 GUID，避免 SBP 卸载后用失效 InstanceID 恢复默认对象和组。</summary>
        private sealed class DefaultAddressablesProtection
        {
            private readonly FieldInfo cacheField;
            private readonly bool hadConfig;
            private readonly string configPath;
            private readonly string configGuid;
            private readonly bool configDirty;
            private readonly bool defaultObjectAssetExisted;
            private readonly string settingsPath;
            private readonly string cachedSettingsPath;
            private readonly string[] groupGuids;
            private readonly Hash128 currentHash;
            private readonly bool settingsDirty;

            internal DefaultAddressablesProtection()
            {
                cacheField = typeof(AddressableAssetSettingsDefaultObject).GetField("s_DefaultSettingsObject", BindingFlags.Static | BindingFlags.NonPublic)
                    ?? throw new InvalidOperationException("Addressables 默认设置缓存入口已改变。");
                var cached = (AddressableAssetSettings)cacheField.GetValue(null);
                cachedSettingsPath = cached == null ? null : AssetDatabase.GetAssetPath(cached);
                string defaultObjectPath = AddressableAssetSettingsDefaultObject.kDefaultConfigFolder + "/DefaultObject.asset";
                defaultObjectAssetExisted = AssetDatabase.LoadMainAssetAtPath(defaultObjectPath) != null;
                hadConfig = EditorBuildSettings.TryGetConfigObject(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName,
                    out AddressableAssetSettingsDefaultObject config);
                if (hadConfig)
                {
                    configPath = AssetDatabase.GetAssetPath(config);
                    if (string.IsNullOrEmpty(configPath)) throw new InvalidDataException("默认 Addressables 选择对象必须为持久资产。");
                    configDirty = EditorUtility.IsDirty(config);
                    using var serialized = new SerializedObject(config);
                    configGuid = serialized.FindProperty("m_AddressableAssetSettingsGuid")?.stringValue
                        ?? throw new InvalidDataException("Addressables 默认设置 GUID 字段已改变。");
                    settingsPath = AssetDatabase.GUIDToAssetPath(configGuid);
                }
                if (!string.IsNullOrEmpty(cachedSettingsPath)) settingsPath = cachedSettingsPath;
                if (string.IsNullOrEmpty(settingsPath) && EditorBuildSettings.TryGetConfigObject(
                    AddressableAssetSettingsDefaultObject.kDefaultConfigAssetName, out AddressableAssetSettings legacy))
                    settingsPath = AssetDatabase.GetAssetPath(legacy);
                if (string.IsNullOrEmpty(settingsPath)) return;
                var settings = AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>(settingsPath);
                if (settings == null) throw new InvalidDataException("默认 Addressables 设置无法读取：" + settingsPath);
                settingsDirty = EditorUtility.IsDirty(settings);
                using var snapshot = new SerializedObject(settings);
                SerializedProperty groups = snapshot.FindProperty("m_GroupAssets");
                SerializedProperty hash = snapshot.FindProperty("m_currentHash");
                if (groups == null || !groups.isArray || hash == null || hash.propertyType != SerializedPropertyType.Hash128)
                    throw new InvalidDataException("Addressables 默认设置序列化结构已改变。");
                groupGuids = new string[groups.arraySize];
                for (int i = 0; i < groups.arraySize; i++)
                {
                    Object group = groups.GetArrayElementAtIndex(i).objectReferenceValue;
                    if (group == null) continue;
                    string groupPath = AssetDatabase.GetAssetPath(group);
                    groupGuids[i] = AssetDatabase.AssetPathToGUID(groupPath);
                    if (string.IsNullOrEmpty(groupGuids[i])) throw new InvalidDataException("默认 Addressables 组必须为持久资产。");
                }
                currentHash = hash.hash128Value;
            }

            internal void Restore()
            {
                // 不使用 Settings setter：它会新建 DefaultObject、SaveAssets，并使原先无默认设置的工程发生变化。
                if (hadConfig)
                {
                    var config = AssetDatabase.LoadAssetAtPath<AddressableAssetSettingsDefaultObject>(configPath);
                    if (config == null) throw new InvalidDataException("默认 Addressables 选择对象已丢失：" + configPath);
                    using var serialized = new SerializedObject(config);
                    serialized.FindProperty("m_AddressableAssetSettingsGuid").stringValue = configGuid;
                    if (serialized.ApplyModifiedPropertiesWithoutUndo()) EditorUtility.SetDirty(config);
                    if (!EditorBuildSettings.TryGetConfigObject(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName,
                        out AddressableAssetSettingsDefaultObject active) || active != config)
                        EditorBuildSettings.AddConfigObject(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName, config, true);
                    if (configDirty) EditorUtility.SetDirty(config);
                    else AssetDatabase.SaveAssetIfDirty(config);
                }
                else
                {
                    EditorBuildSettings.RemoveConfigObject(AddressableAssetSettingsDefaultObject.kDefaultConfigObjectName);
                    cacheField.SetValue(null, null);
                    string path = AddressableAssetSettingsDefaultObject.kDefaultConfigFolder + "/DefaultObject.asset";
                    if (!defaultObjectAssetExisted && AssetDatabase.LoadMainAssetAtPath(path) != null && !AssetDatabase.DeleteAsset(path))
                        throw new IOException("本次生成的默认 Addressables 选择对象清理失败：" + path);
                }
                if (!string.IsNullOrEmpty(settingsPath)) RestoreAddressablesSettings(settingsPath, groupGuids, currentHash, settingsDirty);
                var cached = string.IsNullOrEmpty(cachedSettingsPath) ? null : AssetDatabase.LoadAssetAtPath<AddressableAssetSettings>(cachedSettingsPath);
                if (!string.IsNullOrEmpty(cachedSettingsPath) && cached == null)
                    throw new InvalidDataException("原默认 Addressables 缓存资产已丢失：" + cachedSettingsPath);
                cacheField.SetValue(null, cached);
            }
        }

        /// <summary>URP 14 构建会写入管线裁剪缓存；无论成功或异常都仅恢复这些生成字段。</summary>
        internal sealed class ResourceBuildProtection : IDisposable
        {
            private readonly List<(string assetPath, bool dirty, string[] paths, int[] values)> snapshots = new();
            private readonly string linkPath = Path.Combine(Addressables.BuildPath, "AddressablesLink", "link.xml");
            private readonly byte[] originalLink;
            private readonly Dictionary<string, byte[]> generatedFiles = new(StringComparer.OrdinalIgnoreCase);
            private readonly List<(PropertyInfo property, bool value)> addressablesPreferences = new();
            private readonly DefaultAddressablesProtection defaultAddressables = new();
            internal ResourceBuildProtection()
            {
                EnsurePlainPath(linkPath);
                originalLink = File.Exists(linkPath) ? File.ReadAllBytes(linkPath) : null;
                // SBP 在部分构建回调中也会写默认位置，保护作者工程已有的状态文件。
                TrackGeneratedFile(Path.Combine(AddressableAssetSettingsDefaultObject.kDefaultConfigFolder,
                    PlatformMappingService.GetPlatformPathSubFolder(), "addressables_content_state.bin"));
                TrackGeneratedFile(Path.Combine(Addressables.LibraryPath,
                    PlatformMappingService.GetPlatformPathSubFolder(), "addressables_content_state.bin"));
                foreach (string name in new[] { "GenerateBuildLayout", "AutoOpenAddressablesReport", "UserHasBeenInformedAboutBuildReportSettingPreBuild" })
                {
                    PropertyInfo property = typeof(ProjectConfigData).GetProperty(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property == null || property.PropertyType != typeof(bool) || !property.CanWrite)
                        throw new InvalidOperationException("Addressables 构建偏好入口已改变：" + name);
                    addressablesPreferences.Add((property, (bool)property.GetValue(null)));
                }
                foreach (string guid in AssetDatabase.FindAssets("t:UniversalRenderPipelineAsset"))
                {
                    string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    Object asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    using var serialized = new SerializedObject(asset);
                    var paths = new List<string>();
                    var values = new List<int>();
                    SerializedProperty property = serialized.GetIterator();
                    while (property.Next(true))
                    {
                        if (!property.propertyPath.StartsWith("m_Prefilter", StringComparison.Ordinal)) continue;
                        if (property.propertyType != SerializedPropertyType.Boolean && property.propertyType != SerializedPropertyType.Enum &&
                            property.propertyType != SerializedPropertyType.Integer) throw new InvalidDataException("URP 预筛选字段类型已改变。");
                        paths.Add(property.propertyPath);
                        values.Add(property.propertyType == SerializedPropertyType.Boolean ? (property.boolValue ? 1 : 0) : property.intValue);
                    }
                    snapshots.Add((assetPath, EditorUtility.IsDirty(asset), paths.ToArray(), values.ToArray()));
                }
            }

            internal string TrackRuntimeFile(string relativeName)
            {
                if (Path.IsPathRooted(relativeName) || Path.GetFileName(relativeName) != relativeName)
                    throw new ArgumentException("Addressables 临时运行数据必须使用单个相对文件名。");
                string path = Path.Combine(Addressables.BuildPath, relativeName);
                TrackGeneratedFile(path);
                return path;
            }

            private void TrackGeneratedFile(string path)
            {
                EnsurePlainPath(path);
                if (!generatedFiles.ContainsKey(path)) generatedFiles.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
            }

            internal void PrepareAddressablesPreferences()
            {
                // 首次构建提示和自动打开报告属于作者偏好，临时关闭后完整恢复。
                foreach (var preference in addressablesPreferences)
                    preference.property.SetValue(null, preference.property.Name == "UserHasBeenInformedAboutBuildReportSettingPreBuild");
            }

            internal void RefreshShaderPrefilters()
            {
                Type type = Type.GetType("UnityEditor.Rendering.Universal.ShaderBuildPreprocessor, Unity.RenderPipelines.Universal.Editor", false);
                MethodInfo method = type?.GetMethod("GatherShaderFeatures", BindingFlags.NonPublic | BindingFlags.Static,
                    null, new[] { typeof(bool) }, null) ?? throw new InvalidOperationException("未找到 URP 14 着色器预筛选入口。");
                try { method.Invoke(null, new object[] { false }); }
                catch (TargetInvocationException exception) { throw new InvalidOperationException("Shader 预筛选失败。", exception.InnerException); }
            }

            public void Dispose()
            {
                var failures = new List<Exception>();
                foreach (var snapshot in snapshots)
                {
                    Restore(() =>
                    {
                        Object asset = AssetDatabase.LoadMainAssetAtPath(snapshot.assetPath);
                        if (asset == null) throw new InvalidDataException("URP 恢复资产已丢失：" + snapshot.assetPath);
                        using var serialized = new SerializedObject(asset);
                        for (int i = 0; i < snapshot.paths.Length; i++)
                        {
                            SerializedProperty property = serialized.FindProperty(snapshot.paths[i]);
                            if (property == null) throw new InvalidDataException("URP 恢复字段已丢失。");
                            if (property.propertyType == SerializedPropertyType.Boolean) property.boolValue = snapshot.values[i] != 0;
                            else property.intValue = snapshot.values[i];
                        }
                        if (serialized.ApplyModifiedPropertiesWithoutUndo())
                        {
                            EditorUtility.SetDirty(asset);
                            if (!snapshot.dirty) AssetDatabase.SaveAssetIfDirty(asset);
                        }
                    });
                }
                foreach (var preference in addressablesPreferences)
                    Restore(() => preference.property.SetValue(null, preference.value));
                Restore(() =>
                {
                    if (originalLink == null) { if (File.Exists(linkPath)) File.Delete(linkPath); }
                    else { Directory.CreateDirectory(Path.GetDirectoryName(linkPath)); File.WriteAllBytes(linkPath, originalLink); }
                });
                foreach (var file in generatedFiles)
                    Restore(() =>
                    {
                        if (file.Value == null) { if (File.Exists(file.Key)) File.Delete(file.Key); }
                        else File.WriteAllBytes(file.Key, file.Value);
                    });
                // 临时组删除及其他资产恢复完成后，最后复原默认组列表和构建哈希。
                Restore(defaultAddressables.Restore);
                if (failures.Count > 0) throw new AggregateException("构建设置或临时文件未能完整恢复，请先处理恢复错误再发布。", failures);

                void Restore(Action action)
                {
                    try { action(); }
                    catch (Exception exception) { failures.Add(exception); }
                }
            }
        }
    }
}
