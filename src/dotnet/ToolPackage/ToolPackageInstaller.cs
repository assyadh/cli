﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Microsoft.DotNet.Cli;
using Microsoft.DotNet.Configurer;
using Microsoft.DotNet.Tools;
using Microsoft.Extensions.EnvironmentAbstractions;
using NuGet.Versioning;

namespace Microsoft.DotNet.ToolPackage
{
    internal class ToolPackageInstaller : IToolPackageInstaller
    {
        private readonly IToolPackageStore _store;
        private readonly IProjectRestorer _projectRestorer;
        private readonly FilePath? _tempProject;
        private readonly DirectoryPath _offlineFeed;

        public ToolPackageInstaller(
            IToolPackageStore store,
            IProjectRestorer projectRestorer,
            FilePath? tempProject = null,
            DirectoryPath? offlineFeed = null)
        {
            _store = store ?? throw new ArgumentNullException(nameof(store));
            _projectRestorer = projectRestorer ?? throw new ArgumentNullException(nameof(projectRestorer));
            _tempProject = tempProject;
            _offlineFeed = offlineFeed ?? new DirectoryPath(new CliFolderPathCalculator().CliFallbackFolderPath);
        }

        public IToolPackage InstallPackage(PackageId packageId,
            VersionRange versionRange = null,
            string targetFramework = null,
            FilePath? nugetConfig = null,
            DirectoryPath? rootConfigDirectory = null,
            string[] additionalFeeds = null,
            string verbosity = null)
        {
            System.Console.WriteLine("InstallPackage 1");
            var packageRootDirectory = _store.GetRootPackageDirectory(packageId);
            System.Console.WriteLine("InstallPackage 2 " + packageRootDirectory);
            string rollbackDirectory = null;

            return TransactionalAction.Run<IToolPackage>(
                action: () => {
                    try
                    {
                        var stageDirectory = _store.GetRandomStagingDirectory();

                        Directory.CreateDirectory(stageDirectory.Value);
                        rollbackDirectory = stageDirectory.Value;
                        System.Console.WriteLine("InstallPackage 3 " + rollbackDirectory);
                        var tempProject = CreateTempProject(
                            packageId: packageId,
                            versionRange: versionRange,
                            targetFramework: targetFramework ?? BundledTargetFramework.GetTargetFrameworkMoniker(),
                            restoreDirectory: stageDirectory,
                            rootConfigDirectory: rootConfigDirectory,
                            additionalFeeds: additionalFeeds);
                        System.Console.WriteLine("InstallPackage 4 ");
                        try
                        {
                            System.Console.WriteLine("InstallPackage 5 ");
                            _projectRestorer.Restore(
                                tempProject,
                                stageDirectory,
                                nugetConfig,
                                verbosity: verbosity);
                            System.Console.WriteLine("InstallPackage 6 ");
                        }
                        finally
                        {
                            System.Console.WriteLine("InstallPackage 7 ");
                            File.Delete(tempProject.Value);
                            System.Console.WriteLine("InstallPackage 8 ");
                        }

                        var version = _store.GetStagedPackageVersion(stageDirectory, packageId);
                        System.Console.WriteLine("InstallPackage 9 " + version);
                        var packageDirectory = _store.GetPackageDirectory(packageId, version);
                        System.Console.WriteLine("InstallPackage 10 " + packageDirectory);
                        if (Directory.Exists(packageDirectory.Value))
                        {
                            System.Console.WriteLine("InstallPackage 11");
                            throw new ToolPackageException(
                                string.Format(
                                    CommonLocalizableStrings.ToolPackageConflictPackageId,
                                    packageId,
                                    version.ToNormalizedString()));
                        }

                        Directory.CreateDirectory(packageRootDirectory.Value);
                        System.Console.WriteLine("InstallPackage 12 " + packageRootDirectory.Value);
                        Directory.Move(stageDirectory.Value, packageDirectory.Value);
                        rollbackDirectory = packageDirectory.Value;

                        return new ToolPackageInstance(_store, packageId, version, packageDirectory);
                    }
                    catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException)
                    {
                        System.Console.WriteLine("InstallPackage 13 ");
                        throw new ToolPackageException(
                            string.Format(
                                CommonLocalizableStrings.FailedToInstallToolPackage,
                                packageId,
                                ex.Message),
                            ex);
                    }
                },
                rollback: () => {
                    if (!string.IsNullOrEmpty(rollbackDirectory) && Directory.Exists(rollbackDirectory))
                    {
                        System.Console.WriteLine("InstallPackage 14 ");
                        Directory.Delete(rollbackDirectory, true);
                    }

                    // Delete the root if it is empty
                    if (Directory.Exists(packageRootDirectory.Value) &&
                        !Directory.EnumerateFileSystemEntries(packageRootDirectory.Value).Any())
                    {
                        System.Console.WriteLine("InstallPackage 15");
                        Directory.Delete(packageRootDirectory.Value, false);
                    }
                });
        }

        private FilePath CreateTempProject(PackageId packageId,
            VersionRange versionRange,
            string targetFramework,
            DirectoryPath restoreDirectory,
            DirectoryPath? rootConfigDirectory,
            string[] additionalFeeds)
        {
            var tempProject = _tempProject ?? new DirectoryPath(Path.GetTempPath())
                .WithSubDirectories(Path.GetRandomFileName())
                .WithFile("restore.csproj");

            if (Path.GetExtension(tempProject.Value) != "csproj")
            {
                tempProject = new FilePath(Path.ChangeExtension(tempProject.Value, "csproj"));
            }

            Directory.CreateDirectory(tempProject.GetDirectoryPath().Value);

            var tempProjectContent = new XDocument(
                new XElement("Project",
                    new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                    new XElement("PropertyGroup",
                        new XElement("TargetFramework", targetFramework),
                        new XElement("RestorePackagesPath", restoreDirectory.Value),
                        new XElement("RestoreProjectStyle", "DotnetToolReference"), // without it, project cannot reference tool package
                        new XElement("RestoreRootConfigDirectory", rootConfigDirectory?.Value ?? Directory.GetCurrentDirectory()), // config file probing start directory
                        new XElement("DisableImplicitFrameworkReferences", "true"), // no Microsoft.NETCore.App in tool folder
                        new XElement("RestoreFallbackFolders", "clear"), // do not use fallbackfolder, tool package need to be copied to tool folder
                        new XElement("RestoreAdditionalProjectSources", JoinSourceAndOfflineCache(additionalFeeds)),
                        new XElement("RestoreAdditionalProjectFallbackFolders", string.Empty), // block other
                        new XElement("RestoreAdditionalProjectFallbackFoldersExcludes", string.Empty),  // block other
                        new XElement("DisableImplicitNuGetFallbackFolder", "true")),  // disable SDK side implicit NuGetFallbackFolder
                     new XElement("ItemGroup",
                        new XElement("PackageReference",
                            new XAttribute("Include", packageId.ToString()),
                            new XAttribute("Version",
                                versionRange?.ToString("S", new VersionRangeFormatter()) ?? "*") // nuget will restore latest stable for *
                            ))
                        ));

            File.WriteAllText(tempProject.Value, tempProjectContent.ToString());
            return tempProject;
        }

        private string JoinSourceAndOfflineCache(string[] additionalFeeds)
        {
            var feeds = new List<string>();
            if (additionalFeeds != null)
            {
                foreach (var feed in additionalFeeds)
                {
                    if (Uri.IsWellFormedUriString(feed, UriKind.Absolute))
                    {
                        feeds.Add(feed);
                    }
                    else
                    {
                        feeds.Add(Path.GetFullPath(feed));
                    }
                }
            }

            // use fallbackfolder as feed to enable offline
            if (Directory.Exists(_offlineFeed.Value))
            {
                feeds.Add(_offlineFeed.ToXmlEncodeString());
            }

            return string.Join(";", feeds);
        }
    }
}
