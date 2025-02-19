﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the MIT license.  See License.txt in the project root for license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.Extensions.Logging;

namespace Microsoft.CodeAnalysis.Tools.Analyzers
{
    internal interface ICodeFixApplier
    {
        Task<(Solution Solution, bool NeedsAnotherPass)> ApplyCodeFixesAsync(
            Solution solution,
            CodeAnalysisResult result,
            CodeFixProvider codefixes,
            string diagnosticId,
            ILogger logger,
            CancellationToken cancellationToken);
    }
}
