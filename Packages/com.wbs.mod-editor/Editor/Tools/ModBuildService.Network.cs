using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace WBS.Client.Editor.ModSDK
{
    public static partial class ModBuildService
    {
        /// <summary>只读作者 DLL，不实例化组件或执行静态构造。逐类检查，不能用宿主 WeaverFuse 代替。</summary>
        internal static string[] ValidateNetworkAssemblies(string[] files, string[] references)
        {
            var checkedTypes = new List<string>();
            Assembly cecil = Assembly.Load("Mono.Cecil");
            Type resolverType = cecil.GetType("Mono.Cecil.DefaultAssemblyResolver", true);
            object resolver = Activator.CreateInstance(resolverType);
            try
            {
                string[] paths = files.Concat(references ?? Array.Empty<string>()).Where(File.Exists).ToArray();
                foreach (string directory in paths.Select(path => Path.GetDirectoryName(Path.GetFullPath(path))).Distinct(StringComparer.OrdinalIgnoreCase))
                    resolverType.GetMethod("AddSearchDirectory").Invoke(resolver, new object[] { directory });
                foreach (string file in files)
                {
                    Type readerType = cecil.GetType("Mono.Cecil.ReaderParameters", true);
                    object reader = Activator.CreateInstance(readerType);
                    readerType.GetProperty("AssemblyResolver").SetValue(reader, resolver);
                    readerType.GetProperty("ReadSymbols").SetValue(reader, false);
                    Type assemblyType = cecil.GetType("Mono.Cecil.AssemblyDefinition", true);
                    object definition = assemblyType.GetMethod("ReadAssembly", new[] { typeof(string), readerType })
                        .Invoke(null, new[] { (object)file, reader });
                    try
                    {
                        foreach (object type in EnumerateTypes(Property(Property(definition, "MainModule"), "Types")))
                        {
                            if (!IsNetworkBehaviour(type)) continue;
                            string name = (string)Property(type, "FullName");
                            object[] methods = Items(Property(type, "Methods"));
                            object marker = methods.SingleOrDefault(method => (string)Property(method, "Name") == "Weaved" &&
                                !(bool)Property(method, "IsStatic") && (int)Property(Property(method, "Parameters"), "Count") == 0);
                            string[] markerOps = Instructions(marker).Select(OpCodeName).Where(code => code != "nop").ToArray();
                            if (marker == null || !(bool)Property(marker, "IsVirtual") ||
                                (string)Property(Property(marker, "ReturnType"), "FullName") != "System.Boolean" ||
                                !markerOps.SequenceEqual(new[] { "ldc.i4.1", "ret" }))
                                throw NetworkError(file, name, "本类没有有效 Weaved() 标记，请安装匹配 SDK 的 Mirror ILPP 并重新编译");

                            object[] syncVars = Items(Property(type, "Fields")).Where(field => HasAttribute(field, "Mirror.SyncVarAttribute")).ToArray();
                            if (syncVars.Length > 0)
                            {
                                object serialize = RequireMethod(methods, "SerializeSyncVars", file, name);
                                object deserialize = RequireMethod(methods, "DeserializeSyncVars", file, name);
                                foreach (object field in syncVars)
                                {
                                    string fieldName = (string)Property(field, "Name");
                                    string fieldIdentity = (string)Property(field, "FullName");
                                    if (!ReferencesField(serialize, fieldIdentity, "ldfld") ||
                                        (!ReferencesField(deserialize, fieldIdentity, "ldflda") && !ReferencesField(deserialize, fieldIdentity, "stfld")))
                                        throw NetworkError(file, name, "SyncVar 序列化或读回方法没有操作当前字段：" + fieldName);
                                    object setter = methods.FirstOrDefault(method => (string)Property(method, "Name") == "set_Network" + fieldName);
                                    if (!Calls(setter).Any(call => MethodDeclaringType(call) == "Mirror.NetworkBehaviour" &&
                                        MethodName(call).StartsWith("GeneratedSyncVarSetter", StringComparison.Ordinal)))
                                        throw NetworkError(file, name, "SyncVar 缺少生成的脏位 setter：" + fieldName);
                                }
                            }
                            foreach (object method in methods)
                            {
                                string attribute = HasAttribute(method, "Mirror.CommandAttribute") ? "Command" :
                                    HasAttribute(method, "Mirror.ClientRpcAttribute") ? "ClientRpc" :
                                    HasAttribute(method, "Mirror.TargetRpcAttribute") ? "TargetRpc" : null;
                                if (attribute == null) continue;
                                string methodName = MethodName(method);
                                string suffix = string.Concat(Items(Property(method, "Parameters"))
                                    .Select(parameter => "__" + (string)Property(Property(parameter, "ParameterType"), "Name")));
                                string generated = methodName + suffix;
                                object user = RequireMethod(methods, "UserCode_" + generated, file, name);
                                object invoke = RequireMethod(methods, "InvokeUserCode_" + generated, file, name);
                                string send = attribute == "Command" ? "SendCommandInternal" : attribute == "ClientRpc" ? "SendRPCInternal" : "SendTargetRPCInternal";
                                if (!Calls(method).Any(call => MethodDeclaringType(call) == "Mirror.NetworkBehaviour" && MethodName(call) == send) ||
                                    !Calls(invoke).Any(call => MethodName(call) == MethodName(user)))
                                    throw NetworkError(file, name, attribute + " 缺少发送或读取调用代码：" + methodName);
                                // 静态注册必须同时绑定当前方法的委托及名称，防止另一个 RPC 的注册误充证据。
                                object cctor = methods.FirstOrDefault(candidate => MethodName(candidate) == ".cctor");
                                string register = attribute == "Command" ? "RegisterCommand" : "RegisterRpc";
                                bool delegateBound = Instructions(cctor).Any(instruction => OpCodeName(instruction) == "ldftn" &&
                                    InstructionOperand(instruction) is object operand && MethodName(operand) == MethodName(invoke));
                                bool nameBound = Instructions(cctor).Any(instruction => OpCodeName(instruction) == "ldstr" &&
                                    (string)InstructionOperand(instruction) == (string)Property(method, "FullName"));
                                if (!delegateBound || !nameBound || !Calls(cctor).Any(call =>
                                    MethodDeclaringType(call) == "Mirror.RemoteCalls.RemoteProcedureCalls" && MethodName(call) == register))
                                    throw NetworkError(file, name, attribute + " 缺少远程方法登记：" + methodName);
                            }
                            checkedTypes.Add(Path.GetFileNameWithoutExtension(file) + ":" + name);
                        }
                    }
                    finally { (definition as IDisposable)?.Dispose(); }
                }
            }
            catch (TargetInvocationException error)
            {
                throw new InvalidDataException("无法读取网络代码的完整依赖，请修复 DLL 引用后重新编译。", error.InnerException ?? error);
            }
            finally { (resolver as IDisposable)?.Dispose(); }
            return checkedTypes.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        private static object[] Items(object collection) => ((IEnumerable)collection).Cast<object>().ToArray();
        private static IEnumerable<object> EnumerateTypes(object collection)
        {
            foreach (object type in Items(collection))
            {
                yield return type;
                foreach (object nested in EnumerateTypes(Property(type, "NestedTypes"))) yield return nested;
            }
        }
        private static bool IsNetworkBehaviour(object type)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            object parent = Property(type, "BaseType");
            while (parent != null && visited.Add(Property(parent, "Scope") + ":" + (string)Property(parent, "FullName")))
            {
                string name = (string)Property(parent, "FullName");
                if (name == "Mirror.NetworkBehaviour") return true;
                if (name == "System.Object" || name == "System.ValueType" || name == "System.Enum") return false;
                object resolved = parent.GetType().GetMethod("Resolve", Type.EmptyTypes).Invoke(parent, null);
                if (resolved == null) throw new InvalidDataException("网络继承类型依赖缺失：" + name);
                parent = Property(resolved, "BaseType");
            }
            return false;
        }
        private static bool HasAttribute(object member, string name) => Items(Property(member, "CustomAttributes"))
            .Any(attribute => (string)Property(Property(attribute, "AttributeType"), "FullName") == name);
        private static object[] Instructions(object method) => method != null && (bool)Property(method, "HasBody")
            ? Items(Property(Property(method, "Body"), "Instructions")) : Array.Empty<object>();
        private static string OpCodeName(object instruction) => (string)Property(Property(instruction, "OpCode"), "Name");
        private static object InstructionOperand(object instruction) => Property(instruction, "Operand");
        private static object[] Calls(object method) => Instructions(method).Where(instruction =>
            OpCodeName(instruction) == "call" || OpCodeName(instruction) == "callvirt")
            .Select(InstructionOperand).Where(operand => operand != null).ToArray();
        private static string MethodName(object method) => (string)Property(method, "Name");
        private static string MethodDeclaringType(object method) => (string)Property(Property(method, "DeclaringType"), "FullName");
        private static bool ReferencesField(object method, string fieldIdentity, string operation) => Instructions(method)
            .Any(instruction => OpCodeName(instruction) == operation && InstructionOperand(instruction) is object operand &&
                (string)Property(operand, "FullName") == fieldIdentity);
        private static object RequireMethod(object[] methods, string name, string file, string type) =>
            methods.FirstOrDefault(method => MethodName(method) == name && (bool)Property(method, "HasBody")) ??
            throw NetworkError(file, type, "缺少生成方法：" + name);
        private static InvalidDataException NetworkError(string file, string type, string reason) =>
            new("Mirror 织入检查失败：" + Path.GetFileName(file) + " / " + type + "：" + reason);

        internal static string[] GetNetworkReferencePaths(IEnumerable<UnityEditor.Compilation.Assembly> assemblies) => assemblies
            .SelectMany(assembly => assembly.compiledAssemblyReferences.Concat(new[] { assembly.outputPath }))
            .Concat(AppDomain.CurrentDomain.GetAssemblies().Where(assembly => !assembly.IsDynamic).Select(assembly => assembly.Location))
            .Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

        /// <summary>Prefab 结构检查不触发 Awake；序列化资产由 Unity 加载并回读。</summary>
        internal static void ValidateNetworkPrefabs(string[] assets, List<string> errors)
        {
            foreach (string path in AssetDatabase.GetDependencies(assets, true).Where(path => path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)))
            {
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                if (prefab == null) { errors.Add("Prefab 无法读取：" + path); continue; }
                if (prefab.GetComponentsInChildren<Component>(true).Any(component => component == null)) errors.Add("Prefab 存在缺失脚本：" + path);
                foreach (Mirror.NetworkIdentity identity in prefab.GetComponentsInChildren<Mirror.NetworkIdentity>(true))
                {
                    if (identity.transform != prefab.transform) errors.Add("网络 Prefab 的 NetworkIdentity 必须位于根节点，不能嵌套：" + path);
                    // 不读取 assetId getter：宿主源码的 getter 会调用 Editor SetupIDs，检查必须保持只读。
                    using var serializedIdentity = new SerializedObject(identity);
                    SerializedProperty storedAssetId = serializedIdentity.FindProperty("_assetId");
                    if (storedAssetId == null) { errors.Add("网络 Prefab 缺少 Mirror 身份字段：" + path); continue; }
                    uint assetId = unchecked((uint)storedAssetId.longValue);
                    if (assetId == 0) errors.Add("网络 Prefab 缺少稳定 assetId，请执行 WBS/模组/准备网络Prefab身份后重新检查：" + path);
                    else if (ModNetworkPrefabIdentity.IsModPrefab(path))
                    {
                        try
                        {
                            uint expected = ModNetworkPrefabIdentity.ExpectedAssetId(AssetDatabase.AssetPathToGUID(path));
                            if (assetId != expected) errors.Add("网络 Prefab assetId 与自身资产 GUID 不一致，请准备网络身份后重新检查：" + path);
                            SerializedProperty sceneId = serializedIdentity.FindProperty("sceneId");
                            if (sceneId == null || sceneId.longValue != 0) errors.Add("网络 Prefab 必须使用资源身份且 sceneId 为零：" + path);
                        }
                        catch (InvalidDataException error) { errors.Add("网络 Prefab 身份无效：" + path + " / " + error.Message); }
                    }
                    if (prefab.GetComponentsInChildren<Mirror.NetworkBehaviour>(true).Length > 64) errors.Add("网络 Prefab 超过 Mirror 64 个网络组件上限：" + path);
                }
                foreach (Mirror.NetworkBehaviour behaviour in prefab.GetComponentsInChildren<Mirror.NetworkBehaviour>(true))
                    if (behaviour.GetComponentInParent<Mirror.NetworkIdentity>(true) == null)
                        errors.Add("网络行为缺少所属 NetworkIdentity：" + path + " / " + behaviour.GetType().FullName);
            }
        }
    }
}
