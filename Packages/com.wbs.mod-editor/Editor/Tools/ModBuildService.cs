using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using WBS.Client.Common.Mod;

namespace WBS.Client.Editor.ModSDK
{
    /// <summary>窗口、脚本和批量入口共用的模组构建服务；检查不执行模组入口。</summary>
    public static partial class ModBuildService
    {
        internal static readonly UTF8Encoding Utf8 = new(false);
        public static bool IsBusy { get; private set; }
        public static string ProjectRoot => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        public static IReadOnlyList<string> Check(ModBuildRequest request) => Validate(request).Errors;

        /// <summary>只读预检报告；SDK 清单或路径异常也作为错误返回，供窗口和批量调用统一显示。</summary>
        public static ModBuildReport Validate(ModBuildRequest request)
        {
            var timer = Stopwatch.StartNew();
            var report = new ModBuildReport { UnityVersion = Application.unityVersion, BuildTarget = BuildTarget.StandaloneWindows64.ToString() };
            var errors = new List<string>();
            try { report.SdkVersion = ModSDKEnvironment.InstalledVersion; report.RequiredModApiVersion = ModSDKEnvironment.InstalledModApiVersion; }
            catch (Exception exception) { errors.Add("SDK 信息读取失败：" + exception.Message); }
            try
            {
                string output = ResolveOutputRoot(request);
                ModInfo configured = request?.Profile?.Info;
                report.OutputDirectory = configured != null && Token.IsMatch(configured.KeyName ?? "") && ModCompatibilityManifest.IsVersion(configured.Version)
                    ? Path.Combine(output, configured.KeyName + "-" + configured.Version) : output;
                BuildInputs input = Inspect(request, errors);
                ModInfo info = input.Info ?? configured;
                if (info != null) { report.Uuid = info.UUID; report.KeyName = info.KeyName; report.Version = info.Version; }
                if (!string.IsNullOrEmpty(input.FinalDirectory)) report.OutputDirectory = input.FinalDirectory;
                report.ResourceAddresses = input.Assets ?? Array.Empty<string>();
                report.SharedDependencies = input.SharedDependencies ?? Array.Empty<string>();
                report.Warnings = input.Warnings ?? Array.Empty<string>();
                report.DebugBuild = request?.Profile != null && request.Profile.DebugBuild;
                report.EditorWovenTypes = input.EditorWovenTypes;
            }
            catch (Exception exception) { errors.Add("模组预检失败：" + exception.Message); }
            report.Errors = errors.Distinct().ToArray();
            report.Success = report.Errors.Length == 0;
            report.DurationSeconds = timer.Elapsed.TotalSeconds;
            return report;
        }

        public static async Task<ModBuildReport> BuildAsync(ModBuildRequest request, Action<string> progress = null)
        {
            var errors = new List<string>();
            BuildInputs inputs = Inspect(request, errors);
            if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
            if (IsBusy) throw new InvalidOperationException("已有模组构建正在进行。");
            string jobId = Guid.NewGuid().ToString("N");
            string work = Path.Combine(ProjectRoot, "Library", "ModBuild", jobId);
            string stage = Path.Combine(inputs.OutputRoot, ".mod-build-" + jobId);
            string final = inputs.FinalDirectory;
            var timer = Stopwatch.StartNew();
            var report = new ModBuildReport
            {
                Uuid = inputs.Info.UUID, KeyName = inputs.Info.KeyName, Version = inputs.Info.Version,
                SdkVersion = ModSDKEnvironment.InstalledVersion, UnityVersion = Application.unityVersion,
                RequiredModApiVersion = inputs.Info.RequiredModApiVersion, DebugBuild = inputs.Profile.DebugBuild,
                EditorWovenTypes = inputs.EditorWovenTypes,
                BuildTarget = BuildTarget.StandaloneWindows64.ToString(), OutputDirectory = final,
                ResourceAddresses = inputs.Assets, SharedDependencies = inputs.SharedDependencies, Warnings = inputs.Warnings
            };
            // SDK 清单读取及报告初始化成功后才占用服务，异常不会遗留忙碌状态。
            IsBusy = true;
            try
            {
                EnsurePlainPath(Path.Combine(ProjectRoot, "Logs", "BuildTools"));
                Directory.CreateDirectory(Path.Combine(ProjectRoot, "Logs", "BuildTools"));
                // 与本体构建使用同一文件锁，SDK 项目也不依赖本体编辑器程序集。
                using (var lease = new FileStream(Path.Combine(ProjectRoot, "Logs", "BuildTools", "build.lock"),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                {
                    EnsurePlainPath(inputs.OutputRoot);
                    Directory.CreateDirectory(inputs.OutputRoot);
                    if (Directory.Exists(final) || File.Exists(final)) throw new IOException("发布目录已存在，禁止覆盖：" + final);
                    EnsurePlainPath(work);
                    Directory.CreateDirectory(work);
                    Directory.CreateDirectory(stage);
                    using (var buildSettings = new ModBuildSettingsProtection(inputs.Profile.DebugBuild))
                    {
                        progress?.Invoke("正在编译模组程序集。");
                        report.ExportedAssemblies = await BuildAssemblies(inputs, work, stage, report);
                        if (inputs.Assets.Length > 0)
                        {
                            progress?.Invoke("正在构建独立 Addressables 内容。");
                            BuildResources(inputs, work, stage);
                        }
                    }
                    // 设置恢复必须早于最终目录发布；恢复失败只能留下日志，不能发布半完成包。
                    CopyAdditionalContent(inputs, stage);
                    File.WriteAllText(Path.Combine(stage, "modinfo.json"), JsonUtility.ToJson(inputs.Info, true) + "\n", Utf8);
                    progress?.Invoke("正在校验发布包与全部本地 bundle 依赖。");
                    if (inputs.Assets.Length > 0) ValidatePublishedCatalog(stage, inputs.Assets);
                    if (!ModPublishPackage.TryInspect(stage, out _, out string errorKey))
                        throw new InvalidDataException("发布包检查失败：" + errorKey);
                    report.ResourceSha256 = ModPackageIntegrity.ComputeSha256(stage, inputs.Info.KeyName);
                    report.Success = true;
                    report.DurationSeconds = timer.Elapsed.TotalSeconds;
                    File.WriteAllText(Path.Combine(stage, "mod-build-report.json"), JsonUtility.ToJson(report, true) + "\n", Utf8);
                    // 同卷改名是唯一发布动作；前面的任何失败都不会覆盖旧成品。
                    await PublishOwnedDirectoryAsync(stage, final, inputs.OutputRoot, jobId, report);
                    // 发布已经提交，观察者通知异常不能把已存在的完整成品改报为构建失败。
                    try { progress?.Invoke("模组打包完成：" + final); }
                    catch (Exception notificationError) { AddBuildWarning(report, "发布完成通知失败：" + notificationError.Message); }
                }
                return report;
            }
            catch (Exception exception)
            {
                report.Success = false;
                report.Errors = new[] { exception.Message };
                report.DurationSeconds = timer.Elapsed.TotalSeconds;
                throw;
            }
            finally
            {
                CleanupBuildDirectories(stage, inputs.OutputRoot, jobId, work, Path.Combine(ProjectRoot, "Library", "ModBuild"), report);
                report.DurationSeconds = timer.Elapsed.TotalSeconds;
                // 完整结束报告保留原始构建结果及清理警告；写日志失败也不能覆盖原异常。
                SaveCompletedBuildReport(jobId, report);
            }
        }

        /// <summary>SBP 读取全局 development；编译与资源使用同一选项，异常也原样恢复作者设置。</summary>
        internal sealed class ModBuildSettingsProtection : IDisposable
        {
            private readonly bool previousDevelopment;
            internal ModBuildSettingsProtection(bool debug)
            {
                previousDevelopment = EditorUserBuildSettings.development;
                EditorUserBuildSettings.development = debug;
            }
            public void Dispose() => EditorUserBuildSettings.development = previousDevelopment;
        }

        public static async Task<ModBuildReport[]> BuildBatchAsync(IEnumerable<ModBuildRequest> requests, Action<string> progress = null)
        {
            if (requests == null) throw new ArgumentNullException(nameof(requests));
            ModBuildRequest[] batch = requests.ToArray();
            if (batch.Length == 0) throw new ArgumentException("批次中没有模组。");
            // 全批次先检查，避免明显配置错误在前一包发布后才被发现。
            var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModBuildRequest request in batch)
            {
                var errors = new List<string>();
                BuildInputs input = Inspect(request, errors);
                if (errors.Count != 0) throw new InvalidDataException(string.Join("\n", errors));
                if (!directories.Add(input.FinalDirectory)) throw new InvalidDataException("批次中存在重复发布目录。");
            }
            var reports = new List<ModBuildReport>();
            foreach (ModBuildRequest request in batch) reports.Add(await BuildAsync(request, progress));
            return reports.ToArray();
        }

        /// <summary>-executeMethod WBS.Client.Editor.ModSDK.ModBuildService.BuildFromCommandLine -modProfile Assets/...asset；-modOutput 可临时覆盖输出。</summary>
        public static async void BuildFromCommandLine()
        {
            try
            {
                string[] args = Environment.GetCommandLineArgs();
                string Argument(string flag, bool optional = false)
                {
                    int index = Array.IndexOf(args, flag);
                    if (index < 0 && optional) return null;
                    if (index < 0 || index + 1 >= args.Length) throw new ArgumentException("缺少参数：" + flag);
                    return args[index + 1];
                }
                string[] profiles = Argument("-modProfile").Split(';');
                string output = Argument("-modOutput", true);
                await BuildBatchAsync(profiles.Select(path => new ModBuildRequest
                { Profile = AssetDatabase.LoadAssetAtPath<ModBuildProfile>(path), OutputRoot = output }), UnityEngine.Debug.Log);
                if (Application.isBatchMode) EditorApplication.Exit(0);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError("模组批量构建失败：" + exception);
                if (Application.isBatchMode) EditorApplication.Exit(1);
            }
        }

        public static void EnsurePlainPath(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((Directory.Exists(current) || File.Exists(current)) &&
                    (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("路径不允许符号链接或目录联接：" + current);
                current = Path.GetDirectoryName(current);
            }
        }

        public static bool IsWithin(string path, string parent)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(full, root, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        internal static void DeleteOwnedDirectory(string path, string parent, string expectedName)
        {
            ValidateOwnedDirectory(path, parent, expectedName);
            if (!Directory.Exists(path)) return;
            EnsurePlainDirectoryTree(path);
            Directory.Delete(Path.GetFullPath(path), true);
        }

        private static void ValidateOwnedDirectory(string path, string parent, string expectedName)
        {
            string full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.IsNullOrEmpty(expectedName) || Path.GetFileName(full) != expectedName
                || !string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("临时目录不属于本次构建的直属目录，停止操作。");
            EnsurePlainPath(full);
        }

        private static void EnsurePlainDirectoryTree(string path)
        {
            EnsurePlainPath(path);
            var pending = new Stack<string>();
            pending.Push(Path.GetFullPath(path));
            while (pending.Count > 0)
                foreach (string child in Directory.EnumerateFileSystemEntries(pending.Pop()))
                {
                    var attributes = File.GetAttributes(child);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                        throw new InvalidDataException("临时目录包含链接，停止操作：" + child);
                    if ((attributes & FileAttributes.Directory) != 0) pending.Push(child);
                }
        }

        internal static async Task PublishOwnedDirectoryAsync(string stage, string final, string parent, string jobId,
            ModBuildReport report, Action<string, string> move = null, Func<int, Task> delay = null)
        {
            if (!Guid.TryParseExact(jobId, "N", out _)) throw new InvalidDataException("模组发布 job 身份无效。");
            move ??= Directory.Move;
            delay ??= Task.Delay;
            Exception firstMoveError = null;
            for (int attempt = 0; attempt < 5; attempt++)
            {
                ValidateOwnedDirectory(stage, parent, ".mod-build-" + jobId);
                string finalPath = Path.GetFullPath(final);
                string root = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!string.Equals(Path.GetDirectoryName(finalPath), root, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFullPath(stage), finalPath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("发布目标必须是同一输出目录中的独立成品。");
                EnsurePlainPath(finalPath);
                if (Directory.Exists(finalPath) || File.Exists(finalPath)) throw new IOException("发布目录已存在，禁止覆盖：" + finalPath);
                EnsurePlainDirectoryTree(stage);
                try { move(stage, finalPath); return; }
                catch (Exception error) when (IsRetryablePublishMoveError(error))
                {
                    firstMoveError ??= error;
                    if (attempt == 4) { ExceptionDispatchInfo.Capture(firstMoveError).Throw(); throw; }
                    AddBuildWarning(report, "发布改名暂时受 Windows 文件访问限制，第 " + (attempt + 1)
                        + " 次短暂重试：" + error.Message);
                    await delay((attempt + 1) * 100);
                }
            }
        }

        private static bool IsRetryablePublishMoveError(Exception error)
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || !(error is IOException || error is UnauthorizedAccessException)) return false;
            // 仅 Win32 AccessDenied/SharingViolation/LockViolation；低位相同的其他 HRESULT 不重试。
            uint code = unchecked((uint)error.HResult);
            return code == 0x80070005u || code == 0x80070020u || code == 0x80070021u;
        }

        internal static void CleanupBuildDirectories(string stage, string outputRoot, string jobId, string work, string workParent,
            ModBuildReport report, Action<string, string, string> delete = null)
        {
            delete ??= DeleteOwnedDirectory;
            try
            {
                Cleanup(stage, outputRoot, ".mod-build-" + jobId);
                Cleanup(work, workParent, jobId);
            }
            finally { IsBusy = false; }

            void Cleanup(string path, string parent, string expectedName)
            {
                try
                {
                    ValidateOwnedDirectory(path, parent, expectedName);
                    delete(path, parent, expectedName);
                }
                catch (Exception error)
                {
                    AddBuildWarning(report, "本次构建临时目录清理未完成，已停止清理并保留残留路径：" + path + "；" + error.Message);
                }
            }
        }

        private static void AddBuildWarning(ModBuildReport report, string message)
        {
            report.Warnings = (report.Warnings ?? Array.Empty<string>()).Concat(new[] { message }).Distinct().ToArray();
        }

        internal static void SaveCompletedBuildReport(string jobId, ModBuildReport report, Action<string, string> write = null)
        {
            try
            {
                if (!Guid.TryParseExact(jobId, "N", out _)) throw new InvalidDataException("构建报告 job 身份无效。");
                string logs = Path.Combine(ProjectRoot, "Logs", "ModBuild");
                EnsurePlainPath(logs);
                Directory.CreateDirectory(logs);
                write ??= (path, content) => File.WriteAllText(path, content, Utf8);
                write(Path.Combine(logs, jobId + ".json"), JsonUtility.ToJson(report, true) + "\n");
            }
            catch (Exception error)
            {
                AddBuildWarning(report, "构建结束报告写入失败，原构建结果保持：" + error.Message);
                UnityEngine.Debug.LogWarning(report.Warnings.Last());
            }
        }
    }
}
