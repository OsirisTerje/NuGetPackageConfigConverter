using EnvDTE;
using NuGet.VisualStudio;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace NuGetPackageConfigConverter
{
    [PartCreationPolicy(CreationPolicy.Shared)]
    [Export(typeof(IPackageManagerConverter))]
    public class PackageManagerConverter : IPackageManagerConverter
    {
        private readonly IVsPackageInstaller _installer;
        private readonly IVsPackageUninstaller _uninstaller;
        private readonly IVsPackageRestorer _restorer;
        private readonly IConverterViewProvider _converterViewProvider;
        private readonly IVsPackageInstallerServices _services;

        [ImportingConstructor]
        public PackageManagerConverter(
            IConverterViewProvider converterViewProvider,
            IVsPackageInstallerServices services,
            IVsPackageInstaller installer,
            IVsPackageUninstaller uninstaller,
            IVsPackageRestorer restorer)
        {
            _converterViewProvider = converterViewProvider;
            _installer = installer;
            _services = services;
            _uninstaller = uninstaller;
            _restorer = restorer;
        }

        public bool NeedsConversion(Solution sln) => HasPackageConfig(sln) || HasProjectJson(sln);

        public Task ConvertAsync(Solution sln)
        {
            return _converterViewProvider.ShowAsync(sln, (model, token) =>
            {
                model.Phase = "1/6: Get Projects";
                var projects = sln.GetProjects()
                    .Where(p => HasPackageConfig(p) || HasProjectJson(p))
                    .ToList();

                model.Total = projects.Count * 2 + 1;
                model.IsIndeterminate = false;
                model.Count = 1;

                model.Phase = "2/6: Restore packages in the projects";
                RestoreAll(projects, model);

                model.Phase = "3/6: Remove and cache Packages";
                var packages = RemoveAndCachePackages(projects, model, token);
                token.ThrowIfCancellationRequested();

                model.Phase = "4/6: Remove old dependencyfiles";
                RemoveDependencyFiles(projects, model);

                System.Threading.Thread.Sleep(3000);

                model.Phase = "5/6: Add new 'use packagereference' property to projectfiles";
                RefreshSolution(sln, projects, model);

                System.Threading.Thread.Sleep(3000);

                model.Phase = "6/6: Add packages as packagereferences to projectfiles";
                InstallPackages(projects, packages, model, token);

               


            });
        }

        private void RestoreAll(IEnumerable<Project> projects, ConverterUpdateViewModel model)
        {

            foreach (var project in projects)
            {
                _restorer.RestorePackages(project);
            }
        }

        private IDictionary<string, IEnumerable<PackageConfigEntry>> RemoveAndCachePackages(IEnumerable<Project> projects, ConverterUpdateViewModel model, CancellationToken token)
        {

            var installedPackages = new Dictionary<string, IEnumerable<PackageConfigEntry>>(StringComparer.OrdinalIgnoreCase);
            var projectList = projects.ToList();
            int total = projectList.Count();
            foreach (var project in projectList)
            {
                token.ThrowIfCancellationRequested();

                model.Status = $"{model.Count}/{total}  Retrieving and removing old package format for '{project.Name}'";

                var packages = _services.GetInstalledPackages(project)
                    .Select(p => new PackageConfigEntry(p.Id, p.VersionString))
                    .ToArray();
                var fullname = project.GetFullName();
                if (fullname != null)
                {
                    installedPackages.Add(fullname, packages);

                    RemovePackages(project, packages.Select(p => p.Id), token, model);
                }
                else
                {
                    model.Log = $"{project.Name} not modified, missing fullname";
                }

                model.Count++;
            }

            return installedPackages;
        }

        /// <summary>
        /// Removes packages. Will do a couple of passes in case packages rely on each other
        /// </summary>
        /// <param name="project"></param>
        /// <param name="ids"></param>
        /// <param name="token"></param>
        /// <param name="model"></param>
        /// <returns></returns>
        private bool RemovePackages(Project project, IEnumerable<string> ids, CancellationToken token,
            ConverterUpdateViewModel model)
        {
            var retryCount = new ConcurrentDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var packages = new Queue<string>(ids);
            var maxRetry = packages.Count + 1;
            int maxAttempts = maxRetry * packages.Count;
            int counter = 0;
            while (packages.Count > 0 && counter < maxAttempts)
            {
                counter++;
                token.ThrowIfCancellationRequested();

                var package = packages.Dequeue();

                try
                {
                    model.Log = $"Trying to uninstall {package}    // (counter is {counter})";
                    _uninstaller.UninstallPackage(project, package, false);
                    model.Log = $"Uninstalled {package}";

                }
                catch (Exception e)
                {
                    if (e is InvalidOperationException)
                    {
                        model.Log = $"Invalid operation exception uninstalling {package} ";
                        Debug.WriteLine(e.Message);
                    }
                    else
                    {
                        model.Log = $"Exception uninstalling {package} ";
                        Debug.WriteLine(e);

                    }

                    retryCount.AddOrUpdate(package, 1, (_, count) => count + 1);

                    if (retryCount[package] < maxRetry)
                    {
                        model.Log = $"{package} added back to queue";
                        packages.Enqueue(package);
                    }
                }
            }

            if (counter == maxAttempts)
            {
                model.Log = $"Could not uninstall all packages in {project.Name}";
                System.Threading.Thread.Sleep(2000);
            }

            return !retryCount.Values.Any(v => v >= maxRetry);
        }

        private static bool HasPackageConfig(Solution sln) => sln.GetProjects().Any(p => HasPackageConfig(p));

        private static bool HasPackageConfig(Project project) => GetPackageConfig(project) != null;

        private static bool HasProjectJson(Solution sln) => sln.GetProjects().Any(p => HasProjectJson(p));

        private static bool HasProjectJson(Project project) => GetProjectJson(project) != null;

        private static ProjectItem GetPackageConfig(Project project) => GetProjectItem(project.ProjectItems, "packages.config");

        private void RemoveDependencyFiles(IEnumerable<Project> projects, ConverterUpdateViewModel model)
        {

            foreach (var project in projects)
            {
                model.Status = $"Removing dependency files for '{project.Name}'";
                RemoveDependencyFiles(project);
            }
        }

        private static void RemoveDependencyFiles(Project project)
        {

            GetPackageConfig(project)?.Delete();

            var projectJson = GetProjectJson(project);

            if (projectJson != null)
            {
                var file = Path.Combine(Path.GetDirectoryName(projectJson.FileNames[0]), "project.lock.json");

                projectJson.Delete();

                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }

            project.Save();
        }

        private static void RefreshSolution(Solution sln, IEnumerable<Project> projects, ConverterUpdateViewModel model)
        {
            try
            {


                var projectInfos = projects.Select(p => new ProjectInfo(p.GetFullName(), p.Name)).ToList();
                var slnPath = sln.FullName;

                sln.Close();

                foreach (var project in projectInfos.Where(p => !string.IsNullOrEmpty(p.FullName)))
                {
                    model.Status = $"Fixing restore style in'{project.Name}'";
                    AddRestoreProjectStyle(project.FullName);
                }

                sln.Open(slnPath);

            }
            catch (Exception)
            {
                model.Log = $"Exception while working with restore style property.  Do this manually.";

            }


        }


        class ProjectInfo
        {
            public string FullName { get; }
            public string Name { get; }

            public ProjectInfo(string fullname, string name)
            {
                FullName = fullname;
                Name = name;
            }
        }

        private static void AddRestoreProjectStyle(string path)
        {
            const string NS = "http://schemas.microsoft.com/developer/msbuild/2003";
            var doc = XDocument.Load(path);
            var properties = doc.Descendants(XName.Get("PropertyGroup", NS)).FirstOrDefault();
            properties.LastNode.AddAfterSelf(new XElement(XName.Get("RestoreProjectStyle", NS), "PackageReference"));

            doc.Save(path);
        }

        private void InstallPackages(IEnumerable<Project> projects, IDictionary<string, IEnumerable<PackageConfigEntry>> installedPackages, ConverterUpdateViewModel model, CancellationToken token)
        {
            foreach (var project in projects.Where(p=>p.GetFullName()!=null))
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    if (installedPackages.TryGetValue(project.GetFullName(), out var packages))
                    {
                        model.Status = $"Adding PackageReferences: {project.Name}";

                        foreach (var package in packages)
                        {
                            try
                            {
                                _installer.InstallPackage(null, project, package.Id, package.Version, false);
                            }
                            catch (Exception e)
                            {
                                model.Log = $"Exception installing {package.Id} ({e}";
                            }
                        }

                        model.Count++;
                    }
                }
                catch (NotImplementedException e)
                {
                    Trace.WriteLine(e);
                }
            }
        }

        private static ProjectItem GetProjectJson(Project project)
        {
            var items = project?.ProjectItems;

            if (project == null || items == null)
            {
                return null;
            }

            return GetProjectItem(items, "project.json");
        }

        private static ProjectItem GetProjectItem(ProjectItems items, string name)
        {
            if (items == null)
            {
                return null;
            }

            foreach (ProjectItem item in items)
            {
                if (string.Equals(name, item.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return item;
                }
            }

            return null;
        }
    }



    static class ProjectExtensions
    {
        public static string GetFullName(this Project project)
        {
            try
            {
                return project.FullName;
            }
            catch (Exception )
            {
                return null;
            }
        }
    }
}
