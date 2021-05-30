// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition.Hosting;
using System.Composition.Hosting.Core;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using Moq;

namespace Microsoft.CodeAnalysis.Tools.Workspaces
{
    internal static class MSBuildWorkspaceLoader
    {
        private sealed class SimpleExportDescriptorProvider : ExportDescriptorProvider
        {
            private readonly object _service;

            private readonly Dictionary<string, object> _metadata;

            public SimpleExportDescriptorProvider(string serviceTypeName, object service)
            {
                _service = service;
                _metadata = new Dictionary<string, object>
                {
                    ["ServiceType"] = serviceTypeName,
                    ["Layer"] = ServiceLayer.Host
                };
            }

            public override IEnumerable<ExportDescriptorPromise> GetExportDescriptors(CompositionContract contract, DependencyAccessor descriptorAccessor)
            {
                if (contract.ContractType == typeof(IWorkspaceService))
                {
                    return new[]
                    {
                        new ExportDescriptorPromise(contract, _service.ToString(), true, NoDependencies, _ => ExportDescriptor.Create((c, o) => _service, _metadata))
                    };
                }
                return Enumerable.Empty<ExportDescriptorPromise>();
            }
        }

        // Used in tests for locking around MSBuild invocations
        internal static readonly SemaphoreSlim Guard = new SemaphoreSlim(1, 1);

        public static async Task<Workspace?> LoadAsync(
            string solutionOrProjectPath,
            WorkspaceType workspaceType,
            string? binaryLogPath,
            bool logWorkspaceWarnings,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            var properties = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                // This property ensures that XAML files will be compiled in the current AppDomain
                // rather than a separate one. Any tasks isolated in AppDomains or tasks that create
                // AppDomains will likely not work due to https://github.com/Microsoft/MSBuildLocator/issues/16.
                { "AlwaysCompileMarkupFilesInSeparateDomain", bool.FalseString },
            };

            var t = typeof(TaggedText).Assembly.GetType("Microsoft.CodeAnalysis.CodeActions.WorkspaceServices.ISymbolRenamedCodeActionOperationFactoryWorkspaceService")!;
            var constructor = typeof(Mock<>).MakeGenericType(t).GetConstructor(Type.EmptyTypes)!;
            var instance = constructor.Invoke(null);

            var configuration = new ContainerConfiguration()
                .WithAssemblies(MSBuildMefHostServices.DefaultAssemblies)
                .WithProvider(new SimpleExportDescriptorProvider(t.AssemblyQualifiedName!, ((Mock)instance).Object));

            var hostServices = MefHostServices.Create(configuration.CreateContainer());

            var workspace = MSBuildWorkspace.Create(properties, hostServices);

            Build.Framework.ILogger? binlog = null;
            if (binaryLogPath is not null)
            {
                binlog = new Build.Logging.BinaryLogger()
                {
                    Parameters = binaryLogPath,
                    Verbosity = Build.Framework.LoggerVerbosity.Diagnostic,
                };
            }

            if (workspaceType == WorkspaceType.Solution)
            {
                await workspace.OpenSolutionAsync(solutionOrProjectPath, msbuildLogger: binlog, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                try
                {
                    await workspace.OpenProjectAsync(solutionOrProjectPath, msbuildLogger: binlog, cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (InvalidOperationException)
                {
                    logger.LogError(Resources.Could_not_format_0_Format_currently_supports_only_CSharp_and_Visual_Basic_projects, solutionOrProjectPath);
                    workspace.Dispose();
                    return null;
                }
            }

            LogWorkspaceDiagnostics(logger, logWorkspaceWarnings, workspace.Diagnostics);

            return workspace;

            static void LogWorkspaceDiagnostics(ILogger logger, bool logWorkspaceWarnings, ImmutableList<WorkspaceDiagnostic> diagnostics)
            {
                if (!logWorkspaceWarnings)
                {
                    if (!diagnostics.IsEmpty)
                    {
                        logger.LogWarning(Resources.Warnings_were_encountered_while_loading_the_workspace_Set_the_verbosity_option_to_the_diagnostic_level_to_log_warnings);
                    }

                    return;
                }

                foreach (var diagnostic in diagnostics)
                {
                    if (diagnostic.Kind == WorkspaceDiagnosticKind.Failure)
                    {
                        logger.LogError(diagnostic.Message);
                    }
                    else
                    {
                        logger.LogWarning(diagnostic.Message);
                    }
                }
            }
        }

        /// <summary>
        /// This is for use in tests so that MSBuild is only invoked serially
        /// </summary>
        internal static async Task<Workspace?> LockedLoadAsync(
            string solutionOrProjectPath,
            WorkspaceType workspaceType,
            string? binaryLogPath,
            bool logWorkspaceWarnings,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            await Guard.WaitAsync();
            try
            {
                return await LoadAsync(solutionOrProjectPath, workspaceType, binaryLogPath, logWorkspaceWarnings, logger, cancellationToken);
            }
            finally
            {
                Guard.Release();
            }
        }
    }
}
