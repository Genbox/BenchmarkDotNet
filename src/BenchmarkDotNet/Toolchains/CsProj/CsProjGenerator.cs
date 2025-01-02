using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using System.Xml;
using BenchmarkDotNet.Characteristics;
using BenchmarkDotNet.Extensions;
using BenchmarkDotNet.Helpers;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Locators;
using BenchmarkDotNet.Loggers;
using BenchmarkDotNet.Running;
using BenchmarkDotNet.Toolchains.DotNetCli;
using JetBrains.Annotations;

namespace BenchmarkDotNet.Toolchains.CsProj
{
    [PublicAPI]
    public class CsProjGenerator : DotNetCliGenerator, IEquatable<CsProjGenerator>
    {
        private const string DefaultSdkName = "Microsoft.NET.Sdk";

        private static readonly ImmutableArray<string> SettingsWeWantToCopy = new[]
        {
            "NetCoreAppImplicitPackageVersion",
            "RuntimeFrameworkVersion",
            "PackageTargetFallback",
            "LangVersion",
            "UseWpf",
            "UseWindowsForms",
            "CopyLocalLockFileAssemblies",
            "PreserveCompilationContext",
            "UserSecretsId",
            "EnablePreviewFeatures",
            "RuntimeHostConfigurationOption",
        }.ToImmutableArray();

        public string RuntimeFrameworkVersion { get; }

        public CsProjGenerator(string targetFrameworkMoniker, string cliPath, string packagesPath, string runtimeFrameworkVersion, bool isNetCore = true)
            : base(targetFrameworkMoniker, cliPath, packagesPath, isNetCore)
        {
            RuntimeFrameworkVersion = runtimeFrameworkVersion;
        }

        protected override string GetBuildArtifactsDirectoryPath(BuildPartition buildPartition, string programName)
        {
            string assemblyLocation = buildPartition.RepresentativeBenchmarkCase.Descriptor.Type.Assembly.Location;

            //Assembles loaded from a stream will have an empty location (https://docs.microsoft.com/en-us/dotnet/api/system.reflection.assembly.location).
            string directoryName = assemblyLocation.IsEmpty() ?
                Path.Combine(Directory.GetCurrentDirectory(), "BenchmarkDotNet.Bin") :
                Path.GetDirectoryName(buildPartition.AssemblyLocation);

            return Path.Combine(directoryName, programName);
        }

        protected override string GetProjectFilePath(string buildArtifactsDirectoryPath)
            => Path.Combine(buildArtifactsDirectoryPath, "BenchmarkDotNet.Autogenerated.csproj");

        protected override string GetBinariesDirectoryPath(string buildArtifactsDirectoryPath, string configuration)
            => Path.Combine(buildArtifactsDirectoryPath, "bin", configuration, TargetFrameworkMoniker);

        protected override string GetIntermediateDirectoryPath(string buildArtifactsDirectoryPath, string configuration)
            => Path.Combine(buildArtifactsDirectoryPath, "obj", configuration, TargetFrameworkMoniker);

        [SuppressMessage("ReSharper", "StringLiteralTypo")] // R# complains about $variables$
        protected override void GenerateProject(BuildPartition buildPartition, ArtifactsPaths artifactsPaths, ILogger logger)
        {
            var benchmark = buildPartition.RepresentativeBenchmarkCase;
            var projectFile = GetProjectFilePath(benchmark, logger);

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(projectFile.FullName);
            var (customProperties, sdkName) = GetSettingsThatNeedToBeCopied(xmlDoc, projectFile);

            var content = new StringBuilder(ResourceHelper.LoadTemplate("CsProj.txt"))
                .Replace("$PLATFORM$", buildPartition.Platform.ToConfig())
                .Replace("$CODEFILENAME$", Path.GetFileName(artifactsPaths.ProgramCodePath))
                .Replace("$CSPROJPATH$", projectFile.FullName)
                .Replace("$TFM$", TargetFrameworkMoniker)
                .Replace("$PROGRAMNAME$", artifactsPaths.ProgramName)
                .Replace("$RUNTIMESETTINGS$", GetRuntimeSettings(benchmark.Job.Environment.Gc, buildPartition.Resolver))
                .Replace("$COPIEDSETTINGS$", customProperties)
                .Replace("$CONFIGURATIONNAME$", buildPartition.BuildConfiguration)
                .Replace("$SDKNAME$", sdkName)
                .ToString();

            File.WriteAllText(artifactsPaths.ProjectFilePath, content);
        }

        /// <summary>
        /// returns an MSBuild string that defines Runtime settings
        /// </summary>
        [PublicAPI]
        protected virtual string GetRuntimeSettings(GcMode gcMode, IResolver resolver)
        {
            var builder = new StringBuilder(80)
                .AppendLine("<PropertyGroup>")
                .AppendLine($"<ServerGarbageCollection>{gcMode.ResolveValue(GcMode.ServerCharacteristic, resolver).ToLowerCase()}</ServerGarbageCollection>")
                .AppendLine($"<ConcurrentGarbageCollection>{gcMode.ResolveValue(GcMode.ConcurrentCharacteristic, resolver).ToLowerCase()}</ConcurrentGarbageCollection>");

            if (gcMode.HasValue(GcMode.RetainVmCharacteristic))
                builder.AppendLine($"<RetainVMGarbageCollection>{gcMode.ResolveValue(GcMode.RetainVmCharacteristic, resolver).ToLowerCase()}</RetainVMGarbageCollection>");

            return builder.AppendLine("</PropertyGroup>").ToString();
        }

        // the host project or one of the .props file that it imports might contain some custom settings that needs to be copied, sth like
        // <NetCoreAppImplicitPackageVersion>2.0.0-beta-001607-00</NetCoreAppImplicitPackageVersion>
        // <RuntimeFrameworkVersion>2.0.0-beta-001607-00</RuntimeFrameworkVersion>
        internal (string customProperties, string sdkName) GetSettingsThatNeedToBeCopied(XmlDocument xmlDoc, FileInfo projectFile)
        {
            if (!string.IsNullOrEmpty(RuntimeFrameworkVersion)) // some power users knows what to configure, just do it and copy nothing more
            {
                return (@$"<PropertyGroup>
  <RuntimeFrameworkVersion>{RuntimeFrameworkVersion}</RuntimeFrameworkVersion>
</PropertyGroup>", DefaultSdkName);
            }

            XmlElement projectElement = xmlDoc.DocumentElement;
            // custom SDKs are not added for non-netcoreapp apps (like net471), so when the TFM != netcoreapp we dont parse "<Import Sdk="
            // we don't allow for that mostly to prevent from edge cases like the following
            // <Import Sdk="Microsoft.NET.Sdk.WindowsDesktop" Project="Sdk.props" Condition="'$(TargetFramework)'=='netcoreapp3.0'"/>
            string? sdkName = null;
            if (TargetFrameworkMoniker.StartsWith("netcoreapp", StringComparison.InvariantCultureIgnoreCase))
            {
                foreach (XmlElement importElement in projectElement.GetElementsByTagName("Import"))
                {
                    sdkName = importElement.GetAttribute("Sdk");
                    if (!string.IsNullOrEmpty(sdkName))
                    {
                        break;
                    }
                }
            }
            if (string.IsNullOrEmpty(sdkName))
            {
                sdkName = projectElement.GetAttribute("Sdk");
            }
            // If Sdk isn't an attribute on the Project element, it could be a child element.
            if (string.IsNullOrEmpty(sdkName))
            {
                foreach (XmlElement sdkElement in projectElement.GetElementsByTagName("Sdk"))
                {
                    sdkName = sdkElement.GetAttribute("Name");
                    if (string.IsNullOrEmpty(sdkName))
                    {
                        continue;
                    }
                    string version = sdkElement.GetAttribute("Version");
                    // Version is optional
                    if (!string.IsNullOrEmpty(version))
                    {
                        sdkName += $"/{version}";
                    }
                    break;
                }
            }
            if (string.IsNullOrEmpty(sdkName))
            {
                sdkName = DefaultSdkName;
            }

            XmlDocument? itemGroupsettings = null;
            XmlDocument? propertyGroupSettings = null;

            GetSettingsThatNeedToBeCopied(projectElement, ref itemGroupsettings, ref propertyGroupSettings, projectFile);

            List<string> customSettings = new List<string>(2);
            if (itemGroupsettings != null)
            {
                customSettings.Add(GetIndentedXmlString(itemGroupsettings));
            }
            if (propertyGroupSettings != null)
            {
                customSettings.Add(GetIndentedXmlString(propertyGroupSettings));
            }

            return (string.Join(Environment.NewLine + Environment.NewLine, customSettings), sdkName);
        }

        private static void GetSettingsThatNeedToBeCopied(XmlElement projectElement, ref XmlDocument itemGroupsettings, ref XmlDocument propertyGroupSettings, FileInfo projectFile)
        {
            CopyProperties(projectElement, ref itemGroupsettings, "ItemGroup");
            CopyProperties(projectElement, ref propertyGroupSettings, "PropertyGroup");

            foreach (XmlElement importElement in projectElement.GetElementsByTagName("Import"))
            {
                string propsFilePath = importElement.GetAttribute("Project");
                var directoryName = projectFile.DirectoryName ?? throw new DirectoryNotFoundException(projectFile.DirectoryName);
                string absolutePath = File.Exists(propsFilePath)
                    ? propsFilePath // absolute path or relative to current dir
                    : Path.Combine(directoryName, propsFilePath); // relative to csproj
                if (File.Exists(absolutePath))
                {
                    var importXmlDoc = new XmlDocument();
                    importXmlDoc.Load(absolutePath);
                    GetSettingsThatNeedToBeCopied(importXmlDoc.DocumentElement, ref itemGroupsettings, ref propertyGroupSettings, projectFile);
                }
            }
        }

        private static void CopyProperties(XmlElement projectElement, ref XmlDocument copyToDocument, string groupName)
        {
            XmlElement itemGroupElement = copyToDocument?.DocumentElement;
            foreach (XmlElement groupElement in projectElement.GetElementsByTagName(groupName))
            {
                foreach (var node in groupElement.ChildNodes)
                {
                    if (node is XmlElement setting && SettingsWeWantToCopy.Contains(setting.Name))
                    {
                        if (copyToDocument is null)
                        {
                            copyToDocument = new XmlDocument();
                            itemGroupElement = copyToDocument.CreateElement(groupName);
                            copyToDocument.AppendChild(itemGroupElement);
                        }
                        XmlNode copiedNode = copyToDocument.ImportNode(setting, true);
                        itemGroupElement.AppendChild(copiedNode);
                    }
                }
            }
        }

        private static string GetIndentedXmlString(XmlDocument doc)
        {
            StringBuilder sb = new StringBuilder();
            XmlWriterSettings settings = new XmlWriterSettings
            {
                OmitXmlDeclaration = true,
                Indent = true,
                IndentChars = "  "
            };
            using (XmlWriter writer = XmlWriter.Create(sb, settings))
            {
                doc.Save(writer);
            }
            return sb.ToString();
        }

        /// <summary>
        /// returns a path to the project file which defines the benchmarks
        /// </summary>
        [PublicAPI]
        protected virtual FileInfo GetProjectFilePath(BenchmarkCase benchmark, ILogger logger)
        {
            var notFound = new List<string>();
            var args = new LocatorArgs(benchmark, logger);

            foreach (var locator in benchmark.Config.GetFileLocators())
            {
                if (locator.LocatorType != FileLocatorType.Project)
                {
                    continue;
                }

                if (locator.TryLocate(args, out var fileInfo))
                {
                    if (fileInfo.Exists)
                        return fileInfo;

                    notFound.Add(fileInfo.FullName);
                }
            }

            throw new FileNotFoundException("Unable to find project file. Attempted location(s): " + string.Join(", ", notFound));
        }

        public override bool Equals(object obj) => obj is CsProjGenerator other && Equals(other);

        public bool Equals(CsProjGenerator other)
            => TargetFrameworkMoniker == other.TargetFrameworkMoniker
                && RuntimeFrameworkVersion == other.RuntimeFrameworkVersion
                && CliPath == other.CliPath
                && PackagesPath == other.PackagesPath;

        public override int GetHashCode()
            => HashCode.Combine(TargetFrameworkMoniker, RuntimeFrameworkVersion, CliPath, PackagesPath);
    }
}