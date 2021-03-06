﻿// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Web.LibraryManager.Contracts;
using Newtonsoft.Json;

namespace Microsoft.Web.LibraryManager
{
    /// <summary>
    /// Represents the manifest JSON file and orchestrates the interaction
    /// with the various <see cref="IProvider"/> instances.
    /// </summary>
    public class Manifest
    {
        /// <summary>
        /// Supported versions of Library Manager
        /// </summary>
        public static readonly Version[] SupportedVersions = { new Version("1.0") };
        private IHostInteraction _hostInteraction;
        private readonly List<ILibraryInstallationState> _libraries;
        private IDependencies _dependencies;

        /// <summary>
        /// Creates a new instance of <see cref="Manifest"/>.
        /// </summary>
        /// <param name="dependencies">The host provided dependencies.</param>
        public Manifest(IDependencies dependencies)
        {
            _libraries = new List<ILibraryInstallationState>();
            _dependencies = dependencies;
            _hostInteraction = dependencies?.GetHostInteractions();
        }

        /// <summary>
        /// The version of the <see cref="Manifest"/> document format.
        /// </summary>
        [JsonProperty(ManifestConstants.Version)]
        public string Version { get; set; }

        /// <summary>
        /// The default <see cref="Manifest"/> library provider.
        /// </summary>
        [JsonProperty(ManifestConstants.DefaultProvider)]
        public string DefaultProvider { get; set; }

        /// <summary>
        /// The default destination path for libraries.
        /// </summary>
        [JsonProperty(ManifestConstants.DefaultDestination)]
        public string DefaultDestination { get; set; }

        /// <summary>
        /// A list of libraries contained in the <see cref="Manifest"/>.
        /// </summary>
        [JsonProperty(ManifestConstants.Libraries)]
        [JsonConverter(typeof(LibraryStateTypeConverter))]
        public IEnumerable<ILibraryInstallationState> Libraries => _libraries;

        /// <summary>
        /// Creates a new instance of a <see cref="Manifest"/> class from a file on disk.
        /// </summary>
        /// <remarks>
        /// The <paramref name="fileName"/> doesn't have to exist on disk. It will be created when
        /// <see cref="SaveAsync"/> is invoked.
        /// </remarks>
        /// <param name="fileName">The absolute file path to the manifest JSON file.</param>
        /// <param name="dependencies">The host provided dependencies.</param>
        /// <param name="cancellationToken">A token that allows for cancellation of the operation.</param>
        /// <returns>An instance of the <see cref="Manifest"/> class.</returns>
        public static async Task<Manifest> FromFileAsync(string fileName, IDependencies dependencies, CancellationToken cancellationToken)
        {
            if (File.Exists(fileName))
            {
                string json = await FileHelpers.ReadFileTextAsync(fileName, cancellationToken).ConfigureAwait(false);
                return FromJson(json, dependencies);
            }

            return FromJson("{}", dependencies);
        }

        /// <summary>
        /// Creates an instance of the <see cref="Manifest"/> class based on
        /// the provided JSON string.
        /// </summary>
        /// <param name="json">A string of JSON in the correct format.</param>
        /// <param name="dependencies">The host provided dependencies.</param>
        /// <returns></returns>
        public static Manifest FromJson(string json, IDependencies dependencies)
        {
            try
            {
                Manifest manifest = JsonConvert.DeserializeObject<Manifest>(json);
                manifest._dependencies = dependencies;
                manifest._hostInteraction = dependencies.GetHostInteractions();

                foreach (LibraryInstallationState state in manifest.Libraries.Cast<LibraryInstallationState>())
                {
                    state.ProviderId = state.ProviderId ?? manifest.DefaultProvider;
                    state.DestinationPath = state.DestinationPath ?? manifest.DefaultDestination;
                }

                return manifest;
            }
            catch (Exception)
            {
                dependencies.GetHostInteractions().Logger.Log(PredefinedErrors.ManifestMalformed().Message, LogLevel.Task);
                return null;
            }
        }

        private static bool IsValidManifestVersion(string version)
        {
            try
            {
                return SupportedVersions.Contains(new Version(version));
            }
            catch(Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Adds a library to the <see cref="Libraries"/> collection.
        /// </summary>
        /// <param name="state">An instance of <see cref="ILibraryInstallationState"/> representing the library to add.</param>
        internal void AddLibrary(ILibraryInstallationState state)
        {
            ILibraryInstallationState existing = _libraries.Find(p => p.LibraryId == state.LibraryId && p.ProviderId == state.ProviderId);

            if (existing != null)
                _libraries.Remove(existing);

            _libraries.Add(state);
        }

        /// <summary>
        /// Adds a version to the manifest
        /// </summary>
        /// <param name="version"></param>
        public void AddVersion(string version)
        {
            Version = version;
        }

        /// <summary>
        /// Restores all libraries in the <see cref="Libraries"/> collection.
        /// </summary>
        /// <param name="cancellationToken">A token that allows for cancellation of the operation.</param>
        public async Task<IEnumerable<ILibraryInstallationResult>> RestoreAsync(CancellationToken cancellationToken)
        {
            //TODO: This should have an "undo scope"
            var results = new List<ILibraryInstallationResult>();
            var tasks = new List<Task<ILibraryInstallationResult>>();

            if (!IsValidManifestVersion(Version))
            {
                results.Add(LibraryInstallationResult.FromErrors(new IError[]{ PredefinedErrors.VersionIsNotSupported(Version) }));

                return results;
            }

            foreach (ILibraryInstallationState state in Libraries)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    results.Add(LibraryInstallationResult.FromCancelled(state));
                    _hostInteraction.Logger.Log(Resources.Text.RestoreCancelled, LogLevel.Task);
                    return results;
                }

                if (!state.IsValid(out IEnumerable<IError> errors))
                {
                    results.Add(new LibraryInstallationResult(state, errors.ToArray()));
                    continue;
                }

                _hostInteraction.Logger.Log(string.Format(Resources.Text.RestoringLibrary, state.LibraryId), LogLevel.Operation);

                IProvider provider = _dependencies.GetProvider(state.ProviderId);

                if (provider != null)
                {
                    tasks.Add(provider.InstallAsync(state, cancellationToken));
                }
                else
                {
                    results.Add(new LibraryInstallationResult(state, PredefinedErrors.ProviderUnknown(state.ProviderId)));
                }
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);

            results.AddRange(tasks.Select(t => t.Result));

            return results;
        }

        /// <summary>
        /// Uninstalls the specified library and removes it from the <see cref="Libraries"/> collection.
        /// </summary>
        /// <param name="libraryId">The library identifier.</param>
        /// <param name="deleteFileAction"></param>
        public void Uninstall(string libraryId, Action<string> deleteFileAction)
        {
            ILibraryInstallationState state = Libraries.FirstOrDefault(l => l.LibraryId == libraryId);

            if (state != null)
            {
                DeleteLibraryFiles(state, deleteFileAction);

                _libraries.Remove(state);
            }
        }

        /// <summary>
        /// Saves the manifest file to disk.
        /// </summary>
        /// <param name="fileName">The absolute file path to save the <see cref="Manifest"/> to.</param>
        /// <param name="cancellationToken">A token that allows for cancellation of the operation.</param>
        /// <returns></returns>
        public async Task SaveAsync(string fileName, CancellationToken cancellationToken)
        {
            var settings = new JsonSerializerSettings
            {
                Formatting = Formatting.Indented,
                NullValueHandling = NullValueHandling.Ignore,
            };

            string json = JsonConvert.SerializeObject(this, settings);
            byte[] buffer = Encoding.UTF8.GetBytes(json);

            using (FileStream writer = File.Create(fileName, 4096, FileOptions.Asynchronous))
            {
                await writer.WriteAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        ///  Deletes all library output files from disk.
        /// </summary>
        /// <remarks>
        /// The host calling this method provides the <paramref name="deleteFileAction"/>
        /// that deletes the files from the project.
        /// </remarks>
        /// <param name="deleteFileAction">>An action to delete the files.</param>
        /// <returns></returns>
        public IEnumerable<ILibraryInstallationResult> Clean(Action<string> deleteFileAction)
        {
            List<ILibraryInstallationResult> results = new List<ILibraryInstallationResult>();

            foreach (ILibraryInstallationState state in Libraries)
            {
                results.Add(DeleteLibraryFiles(state, deleteFileAction));
            }

            return results;
        }

        private ILibraryInstallationResult DeleteLibraryFiles(ILibraryInstallationState state, Action<string> deleteFileAction)
        {
            int filesDeleted = 0;

            IProvider provider = _dependencies.GetProvider(state.ProviderId);
            ILibraryInstallationResult updatedStateResult = provider.UpdateStateAsync(state, CancellationToken.None).Result;

            if (updatedStateResult.Success)
            {
                state = updatedStateResult.InstallationState;
                foreach (string file in state.Files)
                {
                    var url = new Uri(file, UriKind.RelativeOrAbsolute);

                    if (!url.IsAbsoluteUri)
                    {
                        string relativePath = Path.Combine(state.DestinationPath, file).Replace('\\', '/');
                        deleteFileAction?.Invoke(relativePath);
                        filesDeleted++;
                    }
                }

                if (state.Files != null && filesDeleted == state.Files.Count())
                {
                    return LibraryInstallationResult.FromSuccess(updatedStateResult.InstallationState);
                }
            }

            return updatedStateResult;
        }
    }
}
