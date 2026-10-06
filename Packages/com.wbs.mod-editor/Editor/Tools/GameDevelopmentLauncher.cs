using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using WBS.Client.Common.Mod;

namespace WBS.Client.Editor.ModSDK
{
    [Serializable]
    public sealed class GameDevelopmentHostInfo
    {
        public int schemaVersion = 3;
        public string sdkVersion;
        public string modApiVersion;
        /// <summary>schema 3 为真实发行号；schema 2 为历史源码基线，不能用于游戏版本要求。</summary>
        public string gameVersion;
        public string unityVersion;
        public string addressablesVersion;
        public string urpVersion;
        public string inputSystemVersion;
        public string executable;
        public string productName;
        public string companyName;
        public string persistentDataDirectoryName;
        public string applicationVersion;
        public string builtAtUtc;
        public string sourceRevision;
        public string buildTarget;
    }

    /// <summary>将完整调试包安装到目标游戏的开发目录，再启动同一个 WBS.exe。</summary>
    public static class GameDevelopmentLauncher
    {
        private sealed class Package
        {
            internal string Source, Digest, Target, Stage, Backup;
            internal ModInfo Info;
            internal bool Installed;
        }

        public static Process Launch(ModBuildProfile profile, ModBuildReport report, IEnumerable<string> additionalPackages = null)
        {
            if (profile == null || report == null || !report.Success || !Directory.Exists(report.OutputDirectory))
                throw new InvalidOperationException("请先成功打包模组，再启动游戏开发模式。");
            string executable = ResolveGame(profile.GameExecutablePath);
            GameDevelopmentHostInfo info = ReadGameInfo(executable);
            var sdk = ModSDKEnvironment.ReadManifest();
            string developmentRoot = ResolveDevelopmentDataRoot(info);
            InstallPackages(developmentRoot, report, info, additionalPackages);
            string session = Path.Combine(developmentRoot, "diagnostics/SDKDebugSessions",
                DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            ModBuildService.EnsurePlainPath(session);
            Directory.CreateDirectory(session);
            string log = Path.Combine(session, "Player.log");
            File.WriteAllText(Path.Combine(session, "session.json"), JsonConvert.SerializeObject(new
            {
                game = executable, gameVersion = info.applicationVersion, applicationVersion = info.applicationVersion,
                sourceRevision = info.sourceRevision, sdkVersion = sdk.sdkVersion,
                recommendedSdkVersion = info.sdkVersion, sdkModApiVersion = sdk.modApiVersion,
                modApiVersion = info.modApiVersion, developmentRoot, log, createdAtUtc = DateTime.UtcNow.ToString("O")
            }, Formatting.Indented));
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Path.GetDirectoryName(executable), UseShellExecute = false,
                Arguments = "--dev -logFile " + Quote(log)
            };
            Process process = Process.Start(start) ?? throw new InvalidOperationException("游戏进程未启动，已安装的开发模组保留。");
            UnityEngine.Debug.Log("游戏开发模式已启动，WBS 进程 " + process.Id + "；开发数据：" + developmentRoot + "；日志：" + log);
            EditorUtility.RevealInFinder(Path.Combine(session, "session.json"));
            return process;
        }

        public static string ResolveGame(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("请选择支持当前模组 API 和工具链的 Windows x64 WBS.exe。");
            string resolved = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(ModBuildService.ProjectRoot, path));
            ModBuildService.EnsurePlainPath(resolved);
            if (!File.Exists(resolved) || !string.Equals(Path.GetFileName(resolved), "WBS.exe", StringComparison.OrdinalIgnoreCase))
                throw new FileNotFoundException("请选择完整游戏目录中的 WBS.exe。", resolved);
            return resolved;
        }

        public static GameDevelopmentHostInfo ReadGameInfo(string executable)
        {
            string metadata = Path.Combine(Path.GetDirectoryName(executable), "game-development.json");
            ModBuildService.EnsurePlainPath(metadata);
            if (!File.Exists(metadata)) throw new InvalidDataException("游戏缺少 game-development.json，请下载包含开发元数据的完整游戏。");
            var info = ParseGameInfo(File.ReadAllText(metadata));
            var sdk = ModSDKEnvironment.ReadManifest();
            if (!ModApiProtocol.Validate(sdk.modApiVersion, info.modApiVersion, out string reason))
                throw new InvalidOperationException("游戏与当前 SDK 的模组 API 不兼容：" + reason);
            ValidateHostToolchain(info.addressablesVersion, sdk.addressablesVersion, "Addressables");
            ValidateHostToolchain(info.urpVersion, sdk.urpVersion, "URP");
            ValidateHostToolchain(info.inputSystemVersion, sdk.inputSystemVersion, "Input System");
            // 推荐只用于提示；源码基线和提交号只供溯源，不能阻止日常游戏更新复用 SDK。
            if (!string.IsNullOrEmpty(info.sdkVersion) && info.sdkVersion != sdk.sdkVersion)
                UnityEngine.Debug.LogWarning("游戏推荐 SDK " + info.sdkVersion + "，当前使用 " + sdk.sdkVersion +
                    "；模组 API 兼容，无需仅因推荐版本不同而升级或重新打包。");
            if (string.IsNullOrEmpty(info.addressablesVersion) || string.IsNullOrEmpty(info.urpVersion) || string.IsNullOrEmpty(info.inputSystemVersion))
                UnityEngine.Debug.LogWarning("旧版游戏开发元数据未记录完整工具链；已核对 Unity、Windows x64 和模组 API，请依据该游戏的兼容验证记录确认其余工具链。");
            return info;
        }

        private static void ValidateHostToolchain(string actual, string expected, string name)
        {
            if (!string.IsNullOrEmpty(actual) && actual != expected)
                throw new InvalidDataException("游戏工具链不受当前 SDK 支持：" + name + " " + actual + "，要求 " + expected + "。");
        }

        internal static GameDevelopmentHostInfo ParseGameInfo(string json)
        {
            JObject document;
            // 协议字段必须保持 JSON 原始类型，避免 ISO 时间被自动转换为 Date。
            using (var text = new StringReader(json))
            using (var reader = new JsonTextReader(text) { DateParseHandling = DateParseHandling.None })
            {
                document = JObject.Load(reader);
                if (reader.Read()) throw new InvalidDataException("游戏开发元数据包含额外 JSON 内容。");
            }
            if (document["schemaVersion"]?.Type != JTokenType.Integer ||
                (document.Value<int>("schemaVersion") != 2 && document.Value<int>("schemaVersion") != 3))
                throw new InvalidDataException("游戏的开发元数据必须明确声明 schemaVersion 3，或旧版 schemaVersion 2。");
            int schema = document.Value<int>("schemaVersion");
            foreach (string field in new[] { "modApiVersion", "gameVersion", "unityVersion", "executable", "productName",
                         "companyName", "persistentDataDirectoryName", "applicationVersion", "builtAtUtc", "sourceRevision", "buildTarget" })
                if (document[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(document.Value<string>(field)))
                    throw new InvalidDataException("游戏开发元数据缺少有效字段：" + field);
            if (document["sdkVersion"]?.Type != JTokenType.String ||
                (!string.IsNullOrEmpty(document.Value<string>("sdkVersion")) && !ModApiProtocol.TryParse(document.Value<string>("sdkVersion"), out _)))
                throw new InvalidDataException("游戏开发元数据 sdkVersion 必须为空或有效的三段推荐 SDK 版本。");
            foreach (string field in new[] { "addressablesVersion", "urpVersion", "inputSystemVersion" })
                if ((schema == 3 || document.Property(field) != null) &&
                    (document[field]?.Type != JTokenType.String || string.IsNullOrWhiteSpace(document.Value<string>(field))))
                    throw new InvalidDataException("游戏开发元数据的工具链字段无效：" + field);
            var info = document.ToObject<GameDevelopmentHostInfo>();
            if (info.productName != "WBS" || info.persistentDataDirectoryName != "WBS" || info.unityVersion != ModSDKEnvironment.UnityVersion ||
                !ModApiProtocol.TryParse(info.modApiVersion, out _) ||
                info.executable != "WBS.exe" || info.buildTarget != "StandaloneWindows64" || !Regex.IsMatch(info.sourceRevision, "^[0-9a-f]{40}$") ||
                !IsApplicationVersion(info.applicationVersion) ||
                (schema == 3 ? info.gameVersion != info.applicationVersion :
                    !info.gameVersion.EndsWith("@" + info.sourceRevision.Substring(0, 8), StringComparison.Ordinal)) ||
                !(info.builtAtUtc.EndsWith("Z", StringComparison.Ordinal) || info.builtAtUtc.EndsWith("+00:00", StringComparison.Ordinal)) ||
                !DateTimeOffset.TryParse(info.builtAtUtc, out var builtAt) || builtAt.Offset != TimeSpan.Zero)
                throw new InvalidDataException("游戏的开发元数据、平台、源码或版本不匹配。");
            ValidateDirectoryName(info.companyName);
            // 旧版 gameVersion 保留原始分支@提交，兼容旧记录回读；会话和版本要求使用 applicationVersion。
            return info;
        }

        private static bool IsApplicationVersion(string value)
        {
            string[] parts = value.Split('.');
            return parts.Length == 3 && parts.All(part => part.Length > 0 && part.All(character => character >= '0' && character <= '9') &&
                int.TryParse(part, out int number) && number <= 65535);
        }

        public static string ResolveDevelopmentDataRoot(GameDevelopmentHostInfo info)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT) throw new PlatformNotSupportedException("当前调试启动仅支持 Windows x64。");
            ValidateDirectoryName(info.companyName);
            ValidateDirectoryName(info.persistentDataDirectoryName);
            // 作者工程的 Unity 产品名与游戏不同，必须从所选游戏元数据定位 LocalLow。
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) throw new InvalidOperationException("无法确定当前 Windows 用户的数据目录。");
            string root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(local), "LocalLow", info.companyName, info.persistentDataDirectoryName + "_Dev"));
            ModBuildService.EnsurePlainPath(root);
            return root;
        }

        private static void ValidateDirectoryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name == "." || name == ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                name.EndsWith(".", StringComparison.Ordinal) || name.EndsWith(" ", StringComparison.Ordinal))
                throw new InvalidDataException("游戏元数据中的数据目录名称无效。");
        }

        internal static void InstallPackages(string root, ModBuildReport report, GameDevelopmentHostInfo host, IEnumerable<string> additionalPackages = null)
        {
            string fullRoot = Path.GetFullPath(root);
            if (!Path.GetFileName(fullRoot).Equals(host.persistentDataDirectoryName + "_Dev", StringComparison.Ordinal))
                throw new InvalidDataException("安装目标必须是所选游戏的开发目录。");
            string mods = Path.Combine(fullRoot, "mod");
            ModBuildService.EnsurePlainPath(mods);
            EnsureGameStopped();
            var incoming = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
            foreach (string source in new[] { report.OutputDirectory }.Concat(additionalPackages ?? Array.Empty<string>()))
            {
                Package package = InspectPackage(source, host);
                if (!incoming.TryAdd(package.Info.UUID, package)) throw new InvalidDataException("调试包 UUID 重复：" + package.Info.UUID);
            }
            Package primary = incoming.Values.First();
            if (!string.Equals(primary.Info.UUID, report.Uuid, StringComparison.OrdinalIgnoreCase) || primary.Info.KeyName != report.KeyName || primary.Info.Version != report.Version ||
                !string.Equals(primary.Digest, report.ResourceSha256, StringComparison.Ordinal))
                throw new InvalidDataException("调试成品与成功构建报告不同，请重新打包。");
            var available = ReadInstalledPackages(mods, host);
            foreach (Package package in incoming.Values)
            {
                if (available.TryGetValue(package.Info.UUID, out Package existing)) package.Target = existing.Source;
                else package.Target = Path.Combine(mods, package.Info.UUID);
                if (!available.ContainsKey(package.Info.UUID) && (Directory.Exists(package.Target) || File.Exists(package.Target)))
                    throw new InvalidDataException("目标目录已存在但没有可确认的同 UUID 模组：" + package.Target);
                available[package.Info.UUID] = package;
            }
            var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Package package in incoming.Values) ValidateDependencies(package, available, done, new HashSet<string>(StringComparer.OrdinalIgnoreCase), host);
            Directory.CreateDirectory(fullRoot);
            // 同一开发目录只允许一个 SDK 安装事务，游戏运行时不替换程序集。
            using var installLock = new FileStream(Path.Combine(fullRoot, ".sdk-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string id = Guid.NewGuid().ToString("N");
            string staging = Path.Combine(fullRoot, ".sdk-install-" + id);
            Directory.CreateDirectory(staging);
            bool preserveRecovery = false;
            try
            {
                foreach (Package package in incoming.Values)
                {
                    package.Stage = Path.Combine(staging, package.Info.UUID);
                    package.Backup = Path.Combine(staging, package.Info.UUID + ".previous");
                    CopyTree(package.Source, package.Stage);
                    if (ModPackageIntegrity.ComputeSha256(package.Source, package.Info.KeyName) != package.Digest ||
                        InspectPackage(package.Stage, host).Digest != package.Digest)
                        throw new IOException("复制过程中调试包发生变化，停止安装：" + package.Source);
                }
                EnsureGameStopped();
                Directory.CreateDirectory(mods);
                // 改名提交；任何包失败时恢复本事务已经替换的包。
                foreach (Package package in incoming.Values)
                {
                    ModBuildService.EnsurePlainPath(package.Target);
                    if (Directory.Exists(package.Target)) Directory.Move(package.Target, package.Backup);
                    Directory.Move(package.Stage, package.Target);
                    package.Installed = true;
                }
            }
            catch
            {
                foreach (Package package in incoming.Values.Reverse())
                {
                    try
                    {
                        if (package.Installed) Directory.Move(package.Target, package.Stage);
                        if (Directory.Exists(package.Backup)) Directory.Move(package.Backup, package.Target);
                    }
                    catch (Exception error)
                    {
                        preserveRecovery = true;
                        UnityEngine.Debug.LogError("开发模组恢复失败，请保留恢复目录 " + staging + "：" + error.Message);
                    }
                }
                throw;
            }
            finally
            {
                if (!preserveRecovery) ModBuildService.DeleteOwnedDirectory(staging, fullRoot, ".sdk-install-" + id);
            }
        }

        private static Dictionary<string, Package> ReadInstalledPackages(string mods, GameDevelopmentHostInfo host)
        {
            var result = new Dictionary<string, Package>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(mods)) return result;
            foreach (string directory in Directory.GetDirectories(mods))
            {
                ModBuildService.EnsurePlainPath(directory);
                if (!File.Exists(Path.Combine(directory, "modinfo.json"))) continue;
                // 先保留旧包的完整结构与身份；协议按本次替换后的实际依赖集合检查。
                Package package = InspectPackage(directory, host, false);
                if (!result.TryAdd(package.Info.UUID, package)) throw new InvalidDataException("开发目录存在同 UUID 的多个模组，请先处理冲突：" + package.Info.UUID);
            }
            return result;
        }

        private static Package InspectPackage(string source, GameDevelopmentHostInfo host, bool validateCompatibility = true)
        {
            string path = Path.GetFullPath(source);
            ModBuildService.EnsurePlainPath(path);
            if (!ModPublishPackage.TryInspect(path, out var inspected, out string error)) throw new InvalidDataException("调试包检查失败：" + error + " / " + path);
            ModInfo info = inspected.Info;
            if (!Guid.TryParseExact(info.UUID, "D", out Guid id)) throw new InvalidDataException("调试包 UUID 无效：" + path);
            info.UUID = id.ToString("D");
            info.Dependencies = info.Dependencies?.Select(value => Guid.TryParseExact(value, "D", out Guid dependency) ? dependency.ToString("D") : value).ToList();
            if (validateCompatibility && !ModApiProtocol.Validate(info.RequiredModApiVersion, host.modApiVersion, out string reason))
                throw new InvalidDataException("游戏与调试包协议不匹配：" + reason + " / " + path);
            return new Package { Source = path, Info = info, Digest = ModPackageIntegrity.ComputeSha256(path, info.KeyName) };
        }

        private static void ValidateDependencies(Package package, Dictionary<string, Package> available, HashSet<string> done, HashSet<string> visiting, GameDevelopmentHostInfo host)
        {
            if (done.Contains(package.Info.UUID)) return;
            if (!ModApiProtocol.Validate(package.Info.RequiredModApiVersion, host.modApiVersion, out string apiReason))
                throw new InvalidDataException("游戏与调试包协议不匹配：" + apiReason + " / " + package.Source);
            if (!visiting.Add(package.Info.UUID)) throw new InvalidDataException("调试包存在循环依赖：" + package.Info.UUID);
            foreach (string id in package.Info.Dependencies ?? new List<string>())
            {
                if (!available.TryGetValue(id, out Package dependency)) throw new InvalidDataException("缺少调试模组依赖，请安装依赖包：" + id);
                ValidateDependencies(dependency, available, done, visiting, host);
            }
            if (!ModPackageIntegrity.ValidateRequirements(package.Info, done, host.applicationVersion, out string reason)) throw new InvalidDataException(reason);
            visiting.Remove(package.Info.UUID);
            done.Add(package.Info.UUID);
        }

        private static void EnsureGameStopped()
        {
            foreach (Process process in Process.GetProcessesByName("WBS"))
                using (process) if (!process.HasExited) throw new InvalidOperationException("请先关闭运行中的 WBS 游戏，再安装调试模组并重启。");
        }

        private static string Quote(string value)
        {
            if (value.IndexOf('"') >= 0 || value.IndexOf('\r') >= 0 || value.IndexOf('\n') >= 0) throw new ArgumentException("启动路径含非法字符。");
            return "\"" + value + "\"";
        }

        private static void CopyTree(string source, string target)
        {
            ModBuildService.EnsurePlainPath(source);
            Directory.CreateDirectory(target);
            foreach (string path in Directory.GetFileSystemEntries(source))
            {
                ModBuildService.EnsurePlainPath(path);
                string destination = Path.Combine(target, Path.GetFileName(path));
                if (Directory.Exists(path)) CopyTree(path, destination); else File.Copy(path, destination, false);
            }
        }
    }
}
