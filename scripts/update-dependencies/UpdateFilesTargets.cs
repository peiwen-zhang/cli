﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.DotNet.Cli.Build.Framework;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NuGet.Versioning;

namespace Microsoft.DotNet.Scripts
{
    public static class UpdateFilesTargets
    {
        private static HttpClient s_client = new HttpClient();

        [Target(nameof(GetDependencies), nameof(ReplaceVersions))]
        public static BuildTargetResult UpdateFiles(BuildTargetContext c) => c.Success();

        /// <summary>
        /// Gets all the dependency information and puts it in the build properties.
        /// </summary>
        [Target]
        public static BuildTargetResult GetDependencies(BuildTargetContext c)
        {
            string coreFxLkgVersion = s_client.GetStringAsync(Config.Instance.CoreFxVersionUrl).Result;
            coreFxLkgVersion = coreFxLkgVersion.Trim();

            const string coreFxIdPattern = @"^(?i)((System\..*)|(NETStandard\.Library)|(Microsoft\.CSharp)|(Microsoft\.NETCore.*)|(Microsoft\.TargetingPack\.Private\.(CoreCLR|NETNative))|(Microsoft\.Win32\..*)|(Microsoft\.VisualBasic))$";
            const string coreFxIdExclusionPattern = @"System.CommandLine";

            List<DependencyInfo> dependencyInfos = c.GetDependencyInfos();
            dependencyInfos.Add(new DependencyInfo()
            {
                Name = "CoreFx",
                IdPattern = coreFxIdPattern,
                IdExclusionPattern = coreFxIdExclusionPattern,
                NewReleaseVersion = coreFxLkgVersion
            });

            return c.Success();
        }

        [Target(nameof(ReplaceProjectJson), nameof(ReplaceCrossGen))]
        public static BuildTargetResult ReplaceVersions(BuildTargetContext c) => c.Success();

        /// <summary>
        /// Replaces all the dependency versions in the project.json files.
        /// </summary>
        [Target]
        public static BuildTargetResult ReplaceProjectJson(BuildTargetContext c)
        {
            List<DependencyInfo> dependencyInfos = c.GetDependencyInfos();

            IEnumerable<string> projectJsonFiles = Enumerable.Union(
                Directory.GetFiles(Dirs.RepoRoot, "project.json", SearchOption.AllDirectories),
                Directory.GetFiles(Path.Combine(Dirs.RepoRoot, @"src\dotnet\commands\dotnet-new"), "project.json.template", SearchOption.AllDirectories));

            JObject projectRoot;
            foreach (string projectJsonFile in projectJsonFiles)
            {
                try
                {
                    projectRoot = ReadProject(projectJsonFile);
                }
                catch (Exception e)
                {
                    c.Warn($"Non-fatal exception occurred reading '{projectJsonFile}'. Skipping file. Exception: {e}. ");
                    continue;
                }

                bool changedAnyPackage = FindAllDependencyProperties(projectRoot)
                    .Select(dependencyProperty => ReplaceDependencyVersion(dependencyProperty, dependencyInfos))
                    .ToArray()
                    .Any(shouldWrite => shouldWrite);

                if (changedAnyPackage)
                {
                    c.Info($"Writing changes to {projectJsonFile}");
                    WriteProject(projectRoot, projectJsonFile);
                }
            }

            return c.Success();
        }

        /// <summary>
        /// Replaces the single dependency with the updated version, if it matches any of the dependencies that need to be updated.
        /// </summary>
        private static bool ReplaceDependencyVersion(JProperty dependencyProperty, List<DependencyInfo> dependencyInfos)
        {
            string id = dependencyProperty.Name;
            foreach (DependencyInfo dependencyInfo in dependencyInfos)
            {
                if (Regex.IsMatch(id, dependencyInfo.IdPattern))
                {
                    if (string.IsNullOrEmpty(dependencyInfo.IdExclusionPattern) || !Regex.IsMatch(id, dependencyInfo.IdExclusionPattern))
                    {
                        string version;
                        if (dependencyProperty.Value is JObject)
                        {
                            version = dependencyProperty.Value["version"].Value<string>();
                        }
                        else if (dependencyProperty.Value is JValue)
                        {
                            version = dependencyProperty.Value.ToString();
                        }
                        else
                        {
                            throw new Exception($"Invalid package project.json version {dependencyProperty}");
                        }

                        VersionRange dependencyVersionRange = VersionRange.Parse(version);
                        NuGetVersion dependencyVersion = dependencyVersionRange.MinVersion;

                        string newReleaseVersion = dependencyInfo.NewReleaseVersion;

                        if (!string.IsNullOrEmpty(dependencyVersion.Release) && dependencyVersion.Release != newReleaseVersion)
                        {
                            string newVersion = new NuGetVersion(
                                dependencyVersion.Major,
                                dependencyVersion.Minor,
                                dependencyVersion.Patch,
                                newReleaseVersion,
                                dependencyVersion.Metadata).ToNormalizedString();

                            if (dependencyProperty.Value is JObject)
                            {
                                dependencyProperty.Value["version"] = newVersion;
                            }
                            else
                            {
                                dependencyProperty.Value = newVersion;
                            }

                            // mark the DependencyInfo as updated so we can tell which dependencies were updated
                            dependencyInfo.IsUpdated = true;

                            return true;
                        }
                    }
                }
            }

            return false;
        }

        private static JObject ReadProject(string projectJsonPath)
        {
            using (TextReader projectFileReader = File.OpenText(projectJsonPath))
            {
                var projectJsonReader = new JsonTextReader(projectFileReader);

                var serializer = new JsonSerializer();
                return serializer.Deserialize<JObject>(projectJsonReader);
            }
        }

        private static void WriteProject(JObject projectRoot, string projectJsonPath)
        {
            string projectJson = JsonConvert.SerializeObject(projectRoot, Formatting.Indented);

            File.WriteAllText(projectJsonPath, projectJson + Environment.NewLine);
        }

        private static IEnumerable<JProperty> FindAllDependencyProperties(JObject projectJsonRoot)
        {
            return projectJsonRoot
                .Descendants()
                .OfType<JProperty>()
                .Where(property => property.Name == "dependencies")
                .Select(property => property.Value)
                .SelectMany(o => o.Children<JProperty>());
        }

        /// <summary>
        /// Replaces version number that is hard-coded in the CrossGen script.
        /// </summary>
        [Target]
        public static BuildTargetResult ReplaceCrossGen(BuildTargetContext c)
        {
            DependencyInfo coreFXInfo = c.GetDependencyInfos().Single(d => d.Name == "CoreFx");

            string compileTargetsPath = Path.Combine(Dirs.RepoRoot, @"scripts\dotnet-cli-build\CompileTargets.cs");
            string compileTargetsContent = File.ReadAllText(compileTargetsPath);

            Regex regex = new Regex(@"CoreCLRVersion = ""(?<version>\d.\d.\d)-(?<release>.*)"";");
            compileTargetsContent = regex.Replace(compileTargetsContent, m =>
            {
                string replacedValue = m.Value;
                Group releaseGroup = m.Groups["release"];

                replacedValue = replacedValue.Remove(releaseGroup.Index - m.Index, releaseGroup.Length);
                replacedValue = replacedValue.Insert(releaseGroup.Index - m.Index, coreFXInfo.NewReleaseVersion);

                return replacedValue;
            });

            File.WriteAllText(compileTargetsPath, compileTargetsContent, Encoding.UTF8);

            return c.Success();
        }
    }
}
