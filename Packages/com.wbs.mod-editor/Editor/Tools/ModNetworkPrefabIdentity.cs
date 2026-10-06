using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Mirror;
using UnityEditor;
using UnityEngine;

namespace WBS.Client.Editor.ModSDK
{
    /// <summary>SDK 使用 Player Mirror DLL，由独立编辑器工具维护模组 Prefab 的持久网络身份。</summary>
    public static class ModNetworkPrefabIdentity
    {
        private static readonly HashSet<string> preparing = new(StringComparer.Ordinal);

        internal static bool IsModPrefab(string path) => path != null && path.StartsWith("Assets/Mod/", StringComparison.Ordinal)
            && path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
            && !path.Split('/').Any(part => part.Length == 0 || part == "." || part == "..");

        [MenuItem("WBS/模组/准备网络Prefab身份")]
        public static void PrepareAll()
        {
            int changed = PrepareFolder("Assets/Mod");
            Debug.Log("模组网络 Prefab 身份准备完成，更新 " + changed + " 个资产。");
        }

        public static int PrepareFolder(string folder)
        {
            if (folder == null || (folder != "Assets/Mod" && !folder.StartsWith("Assets/Mod/", StringComparison.Ordinal))
                || folder.Split('/').Any(part => part.Length == 0 || part == "." || part == ".."))
                throw new ArgumentException("只能准备 Assets/Mod 内的模组资源目录。", nameof(folder));
            if (!AssetDatabase.IsValidFolder(folder)) return 0;
            int changed = 0;
            foreach (string guid in AssetDatabase.FindAssets("t:Prefab", new[] { folder }).OrderBy(value => value, StringComparer.Ordinal))
                if (Ensure(AssetDatabase.GUIDToAssetPath(guid))) changed++;
            return changed;
        }

        /// <summary>同步保存并回读；新建、复制和非零旧身份都按当前资产自己的 GUID 重新计算。</summary>
        public static bool Ensure(string path)
        {
            if (!IsModPrefab(path) || preparing.Contains(path)) return false;
            preparing.Add(path);
            try
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path)
                    ?? throw new InvalidDataException("无法读取模组 Prefab：" + path);
                if (!EditorUtility.IsPersistent(prefab) || !PrefabUtility.IsPartOfPrefabAsset(prefab))
                    throw new InvalidDataException("网络身份只能保存在持久 Prefab 资产上：" + path);
                NetworkIdentity identity = GetRootIdentity(prefab, path);
                if (identity == null) return false;
                string guid = AssetDatabase.AssetPathToGUID(path);
                uint expected = ExpectedAssetId(guid);
                using var serialized = new SerializedObject(identity);
                SerializedProperty assetId = serialized.FindProperty("_assetId");
                SerializedProperty sceneId = serialized.FindProperty("sceneId");
                if (assetId == null || sceneId == null) throw new InvalidDataException("Mirror 网络身份序列化字段缺失：" + path);
                bool changed = assetId.longValue != expected || sceneId.longValue != 0;
                // 宿主的 Mirror OnValidate 可能已修改内存而未写盘，已有正确值也必须保存脏资产。
                if (!changed && !EditorUtility.IsDirty(identity) && !EditorUtility.IsDirty(prefab)) return false;
                if (changed)
                {
                    assetId.longValue = expected;
                    sceneId.longValue = 0;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                PrefabUtility.SavePrefabAsset(prefab, out bool saved);
                if (!saved) throw new IOException("无法保存模组网络 Prefab 身份：" + path);
                GameObject persisted = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                using var readback = new SerializedObject(GetRootIdentity(persisted, path));
                if (AssetDatabase.AssetPathToGUID(path) != guid || readback.FindProperty("_assetId").longValue != expected
                    || readback.FindProperty("sceneId").longValue != 0)
                    throw new InvalidDataException("网络身份保存回读与 Prefab GUID 不一致：" + path);
                return changed;
            }
            finally { preparing.Remove(path); }
        }

        internal static uint ExpectedAssetId(string guid)
        {
            if (!Guid.TryParseExact(guid, "N", out Guid parsed)) throw new InvalidDataException("Prefab 资产 GUID 无效。");
            uint result = NetworkIdentity.AssetGuidToUint(parsed);
            if (result == 0) throw new InvalidDataException("Prefab GUID 映射到空网络身份，请重新创建未发布的新资产。");
            return result;
        }

        internal static NetworkIdentity GetRootIdentity(GameObject prefab, string path)
        {
            if (prefab == null) throw new InvalidDataException("Prefab 无法读取：" + path);
            NetworkIdentity[] identities = prefab.GetComponentsInChildren<NetworkIdentity>(true);
            if (identities.Length == 0) return null;
            if (identities.Length != 1 || identities[0].gameObject != prefab)
                throw new InvalidDataException("网络 Prefab 必须只在根节点具有一个 NetworkIdentity，不能嵌套：" + path);
            return identities[0];
        }
    }

    internal sealed class ModNetworkPrefabIdentityPostprocessor : AssetPostprocessor
    {
        private static void OnPostprocessAllAssets(string[] imported, string[] deleted, string[] moved, string[] movedFrom, bool didDomainReload)
        {
            IEnumerable<string> paths = (imported ?? Array.Empty<string>()).Concat(moved ?? Array.Empty<string>());
            // 首次安装 SDK 时 Prefab 可能先于编辑器源码导入；重载后同步补全已有模组资产。
            if (didDomainReload && AssetDatabase.IsValidFolder("Assets/Mod"))
                paths = paths.Concat(AssetDatabase.FindAssets("t:Prefab", new[] { "Assets/Mod" }).Select(AssetDatabase.GUIDToAssetPath));
            foreach (string path in paths.Where(ModNetworkPrefabIdentity.IsModPrefab).Distinct(StringComparer.Ordinal))
                try { ModNetworkPrefabIdentity.Ensure(path); }
                catch (Exception error) { Debug.LogError("模组网络 Prefab 身份未准备：" + path + "\n" + error.Message); }
        }
    }
}
