﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.MSBuild.Build;
using Microsoft.CodeAnalysis.MSBuild.Logging;
using Roslyn.Utilities;
using MSB = Microsoft.Build;

namespace Microsoft.CodeAnalysis.MSBuild
{
    internal abstract class ProjectFile : IProjectFile
    {
        private readonly ProjectFileLoader _loader;
        private readonly MSB.Evaluation.Project _loadedProject;
        private readonly ProjectBuildManager _buildManager;

        public DiagnosticLog Log { get; }
        public virtual string FilePath => _loadedProject.FullPath;
        public string Language => _loader.Language;

        protected ProjectFile(ProjectFileLoader loader, MSB.Evaluation.Project loadedProject, ProjectBuildManager buildManager, DiagnosticLog log)
        {
            _loader = loader;
            _loadedProject = loadedProject;
            _buildManager = buildManager;
            Log = log;
        }

        ~ProjectFile()
        {
            try
            {
                // unload project so collection will release global strings
                _loadedProject.ProjectCollection.UnloadAllProjects();
            }
            catch
            {
            }
        }

        protected abstract SourceCodeKind GetSourceCodeKind(string documentFileName);
        public abstract string GetDocumentExtension(SourceCodeKind kind);
        protected abstract ProjectFileInfo CreateProjectFileInfo(MSB.Execution.ProjectInstance project);

        public async Task<ImmutableArray<ProjectFileInfo>> GetProjectFileInfosAsync(CancellationToken cancellationToken)
        {
            var targetFrameworkValue = _loadedProject.GetPropertyValue(PropertyNames.TargetFramework);
            var targetFrameworksValue = _loadedProject.GetPropertyValue(PropertyNames.TargetFrameworks);

            if (string.IsNullOrEmpty(targetFrameworkValue) && !string.IsNullOrEmpty(targetFrameworksValue))
            {
                // This project has a <TargetFrameworks> property, but does not specify a <TargetFramework>.
                // In this case, we need to iterate through the <TargetFrameworks>, set <TargetFramework> with
                // each value, and build the project.

                var hasTargetFrameworkProp = _loadedProject.GetProperty(PropertyNames.TargetFramework) != null;
                var targetFrameworks = targetFrameworksValue.Split(';');
                var results = ImmutableArray.CreateBuilder<ProjectFileInfo>(targetFrameworks.Length);

                foreach (var targetFramework in targetFrameworks)
                {
                    _loadedProject.SetProperty(PropertyNames.TargetFramework, targetFramework);
                    _loadedProject.ReevaluateIfNecessary();

                    var projectFileInfo = await BuildProjectFileInfoAsync(cancellationToken).ConfigureAwait(false);

                    results.Add(projectFileInfo);
                }

                // Remove the <TargetFramework> property if it didn't exist in the file before we set it.
                // Otherwise, set it back to it's original value.
                if (!hasTargetFrameworkProp)
                {
                    var targetFrameworkProp = _loadedProject.GetProperty(PropertyNames.TargetFramework);
                    _loadedProject.RemoveProperty(targetFrameworkProp);
                }
                else
                {
                    _loadedProject.SetProperty(PropertyNames.TargetFramework, targetFrameworkValue);
                }

                _loadedProject.ReevaluateIfNecessary();

                return results.ToImmutable();
            }
            else
            {
                var projectFileInfo = await BuildProjectFileInfoAsync(cancellationToken).ConfigureAwait(false);

                return ImmutableArray.Create(projectFileInfo);
            }
        }

        private async Task<ProjectFileInfo> BuildProjectFileInfoAsync(CancellationToken cancellationToken)
        {
            var project = await _buildManager.BuildProjectAsync(_loadedProject, this.Log, cancellationToken).ConfigureAwait(false);

            return project != null
                ? CreateProjectFileInfo(project)
                : ProjectFileInfo.CreateEmpty(this.Language, _loadedProject.FullPath, this.Log);
        }

        protected static string GetProjectDirectory(MSB.Execution.ProjectInstance project)
        {
            var projectDirectory = project.Directory;
            var directorySeparator = Path.DirectorySeparatorChar.ToString();
            if (!projectDirectory.EndsWith(directorySeparator, StringComparison.OrdinalIgnoreCase))
            {
                projectDirectory += directorySeparator;
            }

            return projectDirectory;
        }

        protected static bool IsTemporaryGeneratedFile(string filePath)
        {
            return Path.GetFileName(filePath).StartsWith("TemporaryGeneratedFile_", StringComparison.Ordinal);
        }

        protected DocumentFileInfo MakeDocumentFileInfo(string projectDirectory, MSB.Framework.ITaskItem item)
        {
            var filePath = GetDocumentFilePath(item);
            var logicalPath = GetDocumentLogicalPath(item, projectDirectory);
            var isLinked = IsDocumentLinked(item);
            var isGenerated = IsDocumentGenerated(item);
            var sourceCodeKind = GetSourceCodeKind(filePath);

            return new DocumentFileInfo(filePath, logicalPath, isLinked, isGenerated, sourceCodeKind);
        }

        protected DocumentFileInfo MakeAdditionalDocumentFileInfo(string projectDirectory, MSB.Framework.ITaskItem item)
        {
            var filePath = GetDocumentFilePath(item);
            var logicalPath = GetDocumentLogicalPath(item, projectDirectory);
            var isLinked = IsDocumentLinked(item);
            var isGenerated = IsDocumentGenerated(item);

            return new DocumentFileInfo(filePath, logicalPath, isLinked, isGenerated, SourceCodeKind.Regular);
        }

        protected IEnumerable<ProjectFileReference> GetProjectReferences(MSB.Execution.ProjectInstance executedProject)
        {
            return executedProject
                .GetItems(ItemNames.ProjectReference)
                .Where(i => !i.IsReferenceOutputAssembly())
                .Select(CreateProjectFileReference);
        }

        /// <summary>
        /// Create a <see cref="ProjectFileReference"/> from a ProjectReference node in the MSBuild file.
        /// </summary>
        protected virtual ProjectFileReference CreateProjectFileReference(MSB.Execution.ProjectItemInstance reference)
        {
            return new ProjectFileReference(
                path: reference.EvaluatedInclude,
                aliases: ImmutableArray<string>.Empty);
        }

        protected abstract IEnumerable<MSB.Framework.ITaskItem> GetCommandLineArgsFromModel(MSB.Execution.ProjectInstance executedProject);

        public MSB.Evaluation.ProjectProperty GetProperty(string name)
        {
            return _loadedProject.GetProperty(name);
        }

        /// <summary>
        /// Resolves the given path that is possibly relative to the project directory.
        /// </summary>
        /// <remarks>
        /// The resulting path is absolute but might not be normalized.
        /// </remarks>
        protected string GetAbsolutePath(string path)
        {
            // TODO (tomat): should we report an error when drive-relative path (e.g. "C:goo.cs") is encountered?
            return Path.GetFullPath(FileUtilities.ResolveRelativePath(path, _loadedProject.DirectoryPath) ?? path);
        }

        protected string GetDocumentFilePath(MSB.Framework.ITaskItem documentItem)
        {
            return GetAbsolutePath(documentItem.ItemSpec);
        }

        protected static bool IsDocumentLinked(MSB.Framework.ITaskItem documentItem)
        {
            return !string.IsNullOrEmpty(documentItem.GetMetadata(MetadataNames.Link));
        }

        private IDictionary<string, MSB.Evaluation.ProjectItem> _documents;

        protected bool IsDocumentGenerated(MSB.Framework.ITaskItem documentItem)
        {
            if (_documents == null)
            {
                _documents = new Dictionary<string, MSB.Evaluation.ProjectItem>();
                foreach (var item in _loadedProject.GetItems(ItemNames.Compile))
                {
                    _documents[GetAbsolutePath(item.EvaluatedInclude)] = item;
                }
            }

            return !_documents.ContainsKey(GetAbsolutePath(documentItem.ItemSpec));
        }

        protected static string GetDocumentLogicalPath(MSB.Framework.ITaskItem documentItem, string projectDirectory)
        {
            var link = documentItem.GetMetadata(MetadataNames.Link);
            if (!string.IsNullOrEmpty(link))
            {
                // if a specific link is specified in the project file then use it to form the logical path.
                return link;
            }
            else
            {
                var result = documentItem.ItemSpec;
                if (Path.IsPathRooted(result))
                {
                    // If we have an absolute path, there are two possibilities:
                    result = Path.GetFullPath(result);

                    // If the document is within the current project directory (or subdirectory), then the logical path is the relative path 
                    // from the project's directory.
                    if (result.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase))
                    {
                        result = result.Substring(projectDirectory.Length);
                    }
                    else
                    {
                        // if the document lies outside the project's directory (or subdirectory) then place it logically at the root of the project.
                        // if more than one document ends up with the same logical name then so be it (the workspace will survive.)
                        return Path.GetFileName(result);
                    }
                }

                return result;
            }
        }

        public void AddDocument(string filePath, string logicalPath = null)
        {
            var relativePath = PathUtilities.GetRelativePath(_loadedProject.DirectoryPath, filePath);

            Dictionary<string, string> metadata = null;
            if (logicalPath != null && relativePath != logicalPath)
            {
                metadata = new Dictionary<string, string>();
                metadata.Add(MetadataNames.Link, logicalPath);
                relativePath = filePath; // link to full path
            }

            _loadedProject.AddItem(ItemNames.Compile, relativePath, metadata);
        }

        public void RemoveDocument(string filePath)
        {
            var relativePath = PathUtilities.GetRelativePath(_loadedProject.DirectoryPath, filePath);

            var items = _loadedProject.GetItems(ItemNames.Compile);
            var item = items.FirstOrDefault(it => PathUtilities.PathsEqual(it.EvaluatedInclude, relativePath)
                                               || PathUtilities.PathsEqual(it.EvaluatedInclude, filePath));
            if (item != null)
            {
                _loadedProject.RemoveItem(item);
            }
        }

        public void AddMetadataReference(MetadataReference reference, AssemblyIdentity identity)
        {
            if (reference is PortableExecutableReference peRef && peRef.FilePath != null)
            {
                var metadata = new Dictionary<string, string>();
                if (!peRef.Properties.Aliases.IsEmpty)
                {
                    metadata.Add(MetadataNames.Aliases, string.Join(",", peRef.Properties.Aliases));
                }

                if (IsInGAC(peRef.FilePath) && identity != null)
                {
                    // Since the location of the reference is in GAC, need to use full identity name to find it again.
                    // This typically happens when you base the reference off of a reflection assembly location.
                    _loadedProject.AddItem(ItemNames.Reference, identity.GetDisplayName(), metadata);
                }
                else if (IsFrameworkReferenceAssembly(peRef.FilePath))
                {
                    // just use short name since this will be resolved by msbuild relative to the known framework reference assemblies.
                    var fileName = identity != null ? identity.Name : Path.GetFileNameWithoutExtension(peRef.FilePath);
                    _loadedProject.AddItem(ItemNames.Reference, fileName, metadata);
                }
                else // other location -- need hint to find correct assembly
                {
                    var relativePath = PathUtilities.GetRelativePath(_loadedProject.DirectoryPath, peRef.FilePath);
                    var fileName = Path.GetFileNameWithoutExtension(peRef.FilePath);
                    metadata.Add(MetadataNames.HintPath, relativePath);
                    _loadedProject.AddItem(ItemNames.Reference, fileName, metadata);
                }
            }
        }

        private bool IsInGAC(string filePath)
        {
            return GlobalAssemblyCacheLocation.RootLocations.Any(gloc => PathUtilities.IsChildPath(gloc, filePath));
        }

        private static string s_frameworkRoot;
        private static string FrameworkRoot
        {
            get
            {
                if (string.IsNullOrEmpty(s_frameworkRoot))
                {
                    var runtimeDir = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory();
                    s_frameworkRoot = Path.GetDirectoryName(runtimeDir); // back out one directory level to be root path of all framework versions
                }

                return s_frameworkRoot;
            }
        }

        private bool IsFrameworkReferenceAssembly(string filePath)
        {
            return PathUtilities.IsChildPath(FrameworkRoot, filePath);
        }

        public void RemoveMetadataReference(MetadataReference reference, AssemblyIdentity identity)
        {
            if (reference is PortableExecutableReference peRef && peRef.FilePath != null)
            {
                var item = FindReferenceItem(identity, peRef.FilePath);
                if (item != null)
                {
                    _loadedProject.RemoveItem(item);
                }
            }
        }

        private MSB.Evaluation.ProjectItem FindReferenceItem(AssemblyIdentity identity, string filePath)
        {
            var references = _loadedProject.GetItems(ItemNames.Reference);
            MSB.Evaluation.ProjectItem item = null;

            var fileName = Path.GetFileNameWithoutExtension(filePath);

            if (identity != null)
            {
                var shortAssemblyName = identity.Name;
                var fullAssemblyName = identity.GetDisplayName();

                // check for short name match
                item = references.FirstOrDefault(it => string.Compare(it.EvaluatedInclude, shortAssemblyName, StringComparison.OrdinalIgnoreCase) == 0);

                // check for full name match
                if (item == null)
                {
                    item = references.FirstOrDefault(it => string.Compare(it.EvaluatedInclude, fullAssemblyName, StringComparison.OrdinalIgnoreCase) == 0);
                }
            }

            // check for file path match
            if (item == null)
            {
                var relativePath = PathUtilities.GetRelativePath(_loadedProject.DirectoryPath, filePath);

                item = references.FirstOrDefault(it => PathUtilities.PathsEqual(it.EvaluatedInclude, filePath)
                                                    || PathUtilities.PathsEqual(it.EvaluatedInclude, relativePath)
                                                    || PathUtilities.PathsEqual(GetHintPath(it), filePath)
                                                    || PathUtilities.PathsEqual(GetHintPath(it), relativePath));
            }

            // check for partial name match
            if (item == null && identity != null)
            {
                var partialName = identity.Name + ",";
                var items = references.Where(it => it.EvaluatedInclude.StartsWith(partialName, StringComparison.OrdinalIgnoreCase)).ToList();
                if (items.Count == 1)
                {
                    item = items[0];
                }
            }

            return item;
        }

        private static string GetHintPath(MSB.Evaluation.ProjectItem item)
            => item.Metadata.FirstOrDefault(m => string.Equals(m.Name, MetadataNames.HintPath, StringComparison.OrdinalIgnoreCase))?.EvaluatedValue ?? string.Empty;

        public void AddProjectReference(string projectName, ProjectFileReference reference)
        {
            var metadata = new Dictionary<string, string>();
            metadata.Add(MetadataNames.Name, projectName);

            if (!reference.Aliases.IsEmpty)
            {
                metadata.Add(MetadataNames.Aliases, string.Join(",", reference.Aliases));
            }

            var relativePath = PathUtilities.GetRelativePath(_loadedProject.DirectoryPath, reference.Path);
            _loadedProject.AddItem(ItemNames.ProjectReference, relativePath, metadata);
        }

        public void RemoveProjectReference(string projectName, string projectFilePath)
        {
            var relativePath = PathUtilities.GetRelativePath(_loadedProject.DirectoryPath, projectFilePath);
            var item = FindProjectReferenceItem(projectName, projectFilePath);
            if (item != null)
            {
                _loadedProject.RemoveItem(item);
            }
        }

        private MSB.Evaluation.ProjectItem FindProjectReferenceItem(string projectName, string projectFilePath)
        {
            var references = _loadedProject.GetItems(ItemNames.ProjectReference);
            var relativePath = PathUtilities.GetRelativePath(_loadedProject.DirectoryPath, projectFilePath);

            MSB.Evaluation.ProjectItem item = null;

            // find by project file path
            item = references.First(it => PathUtilities.PathsEqual(it.EvaluatedInclude, relativePath)
                                       || PathUtilities.PathsEqual(it.EvaluatedInclude, projectFilePath));

            // try to find by project name
            if (item == null)
            {
                item = references.First(it => string.Compare(projectName, it.GetMetadataValue(MetadataNames.Name), StringComparison.OrdinalIgnoreCase) == 0);
            }

            return item;
        }

        public void AddAnalyzerReference(AnalyzerReference reference)
        {
            if (reference is AnalyzerFileReference fileRef)
            {
                var relativePath = PathUtilities.GetRelativePath(_loadedProject.DirectoryPath, fileRef.FullPath);
                _loadedProject.AddItem(ItemNames.Analyzer, relativePath);
            }
        }

        public void RemoveAnalyzerReference(AnalyzerReference reference)
        {
            if (reference is AnalyzerFileReference fileRef)
            {
                var relativePath = PathUtilities.GetRelativePath(_loadedProject.DirectoryPath, fileRef.FullPath);

                var analyzers = _loadedProject.GetItems(ItemNames.Analyzer);
                var item = analyzers.FirstOrDefault(it => PathUtilities.PathsEqual(it.EvaluatedInclude, relativePath)
                                                       || PathUtilities.PathsEqual(it.EvaluatedInclude, fileRef.FullPath));
                if (item != null)
                {
                    _loadedProject.RemoveItem(item);
                }
            }
        }

        public void Save()
        {
            _loadedProject.Save();
        }
    }
}
