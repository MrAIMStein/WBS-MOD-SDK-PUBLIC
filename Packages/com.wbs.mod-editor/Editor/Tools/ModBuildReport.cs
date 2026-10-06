using System;

namespace WBS.Client.Editor.ModSDK
{
    /// <summary>构建证据不加入运行时身份；安装包仍沿用 modinfo.json 与 catalog.json。</summary>
    [Serializable]
    public sealed class ModBuildReport
    {
        public int SchemaVersion = 2;
        public bool Success;
        public string Uuid;
        public string KeyName;
        public string Version;
        public string SdkVersion;
        public string RequiredModApiVersion;
        public bool DebugBuild;
        public string UnityVersion;
        public string BuildTarget;
        public string OutputDirectory;
        public string ResourceSha256;
        public string[] ResourceAddresses = Array.Empty<string>();
        public string[] ExportedAssemblies = Array.Empty<string>();
        public string[] DebugSymbols = Array.Empty<string>();
        public string[] EditorWovenTypes = Array.Empty<string>();
        public string[] PlayerWovenTypes = Array.Empty<string>();
        public string[] SharedDependencies = Array.Empty<string>();
        public string[] Warnings = Array.Empty<string>();
        public string[] Errors = Array.Empty<string>();
        public double DurationSeconds;
    }
}
