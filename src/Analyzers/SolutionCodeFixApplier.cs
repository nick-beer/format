﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.Extensions.Logging;

namespace Microsoft.CodeAnalysis.Tools.Analyzers
{
    internal class SolutionCodeFixApplier : ICodeFixApplier
    {
        public async Task<(Solution Solution, bool NeedsAnotherPass)> ApplyCodeFixesAsync(
            Solution solution,
            CodeAnalysisResult result,
            CodeFixProvider codeFix,
            string diagnosticId,
            ILogger logger,
            CancellationToken cancellationToken)
        {
            try
            {
                var diagnostics = result.Diagnostics
                    .SelectMany(kvp => kvp.Value)
                    .Where(diagnostic => diagnostic.Location.SourceTree != null);

                var diagnostic = diagnostics.FirstOrDefault();

                var fixAllProvider = codeFix.GetFixAllProvider();
                if (fixAllProvider?.GetSupportedFixAllScopes()?.Contains(FixAllScope.Solution) != true)
                {
                    if (diagnosticId == "IDE1006")
                    {
                        return await ApplyPrivateFieldNamingFixes(solution, logger, diagnostics, codeFix, cancellationToken);
                    }

                    logger.LogWarning(Resources.Unable_to_fix_0_Code_fix_1_doesnt_support_Fix_All_in_Solution, diagnosticId, codeFix.GetType().Name);
                    return (solution, false);
                }

                if (diagnostic is null)
                {
                    return (solution, false);
                }

                var document = solution.GetDocument(diagnostic.Location.SourceTree);

                if (document is null)
                {
                    return (solution, false);
                }

                CodeAction? action = null;
                var context = new CodeFixContext(document, diagnostic,
                    (a, _) =>
                    {
                        if (action == null)
                        {
                            action = a;
                        }
                    },
                    cancellationToken);

                await codeFix.RegisterCodeFixesAsync(context).ConfigureAwait(false);

                var fixAllContext = new FixAllContext(
                    document: document,
                    codeFixProvider: codeFix,
                    scope: FixAllScope.Solution,
                    codeActionEquivalenceKey: action?.EquivalenceKey!, // FixAllState supports null equivalence key. This should still be supported.
                    diagnosticIds: new[] { diagnosticId },
                    fixAllDiagnosticProvider: new DiagnosticProvider(result),
                    cancellationToken: cancellationToken);

                var fixAllAction = await fixAllProvider.GetFixAsync(fixAllContext).ConfigureAwait(false);
                if (fixAllAction is null)
                {
                    logger.LogWarning(Resources.Unable_to_fix_0_Code_fix_1_didnt_return_a_Fix_All_action, diagnosticId, codeFix.GetType().Name);
                    return (solution, false);
                }

                var operations = await fixAllAction.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
                var applyChangesOperation = operations.OfType<ApplyChangesOperation>().SingleOrDefault();
                if (applyChangesOperation is null)
                {
                    logger.LogWarning(Resources.Unable_to_fix_0_Code_fix_1_returned_an_unexpected_operation, diagnosticId, codeFix.GetType().Name);
                    return (solution, false);
                }

                return (applyChangesOperation.ChangedSolution, false);
            }
            catch (Exception ex)
            {
                logger.LogWarning(Resources.Failed_to_apply_code_fix_0_for_1_2, codeFix?.GetType().Name, diagnosticId, ex.Message);
                return (solution, false);
            }
        }

        private async Task<(Solution Solution, bool NeedsAnotherPass)> ApplyPrivateFieldNamingFixes(
            Solution solution,
            ILogger logger,
            IEnumerable<Diagnostic> diagnostics,
            CodeFixProvider codeFix,
            CancellationToken cancellationToken)
        {
            var madeChanges = false;
            var failedDiagnostics = 0;
            var pairs = diagnostics
                .Select(d => (Diagnostic: d, Document: solution.GetDocument(d.Location.SourceTree)))
                .Where(pair => pair.Document is not null && IsPrivateFieldStyleDiagnostic(pair.Diagnostic))
                .ToList();
            pairs.Sort(SortPairsDescending);
            foreach (var (diagnostic, originatingDocument) in pairs)
            {
                var document = solution.GetDocument(originatingDocument.Id);
                if (document is null)
                {
                    logger.LogWarning($"Failed to get document for diagnostic: {diagnostic}");
                    failedDiagnostics++;
                    continue;
                }

                var id = document.Id;

                CodeAction? action = null;
                var context = new CodeFixContext(document, diagnostic, (a, _) => action ??= a, cancellationToken);

                await codeFix.RegisterCodeFixesAsync(context).ConfigureAwait(false);

                if (action is null)
                {
                    logger.LogWarning($"Failed to get CodeAction for diagnostic: {diagnostic}");
                    failedDiagnostics++;
                    continue;
                }

                var operations = await action.GetOperationsAsync(cancellationToken).ConfigureAwait(false);
                var applyChangesOperation = operations.OfType<ApplyChangesOperation>().SingleOrDefault();
                if (applyChangesOperation is not null)
                {
                    var changedSolution = applyChangesOperation.ChangedSolution;
                    if (changedSolution.GetChanges(solution).Any())
                    {
                        madeChanges = true;
                    }
                    solution = changedSolution;
                }
            }
            return (solution, NeedsAnotherPass: madeChanges && failedDiagnostics > 0);

            int SortPairsDescending((Diagnostic Diagnostic, Document? Document) left, (Diagnostic Diagnostic, Document? Document) right)
                => right.Diagnostic.Location.SourceSpan.CompareTo(left.Diagnostic.Location.SourceSpan);

            bool IsPrivateFieldStyleDiagnostic(Diagnostic diagnostic)
                => diagnostic.Properties.TryGetValue("NamingStyle", out var xml)
                && xml is not null
                && XElement.Parse(xml).Attribute("Name")?.Value == "private_field_style";
        }

        private class DiagnosticProvider : FixAllContext.DiagnosticProvider
        {
            private static Task<IEnumerable<Diagnostic>> EmptyDignosticResult => Task.FromResult(Enumerable.Empty<Diagnostic>());
            private readonly IReadOnlyDictionary<Project, List<Diagnostic>> _diagnosticsByProject;

            internal DiagnosticProvider(CodeAnalysisResult analysisResult)
            {
                _diagnosticsByProject = analysisResult.Diagnostics;
            }

            public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            {
                return GetProjectDiagnosticsAsync(project, cancellationToken);
            }

            public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
            {
                var projectDiagnostics = await GetProjectDiagnosticsAsync(document.Project, cancellationToken);
                return projectDiagnostics.Where(diagnostic => diagnostic.Location.SourceTree?.FilePath == document.FilePath).ToImmutableArray();
            }

            public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            {
                return _diagnosticsByProject.ContainsKey(project)
                    ? Task.FromResult<IEnumerable<Diagnostic>>(_diagnosticsByProject[project])
                    : EmptyDignosticResult;
            }
        }
    }
}
