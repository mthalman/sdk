// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#nullable disable

using System.Text;
using NuGet.RuntimeModel;

namespace Microsoft.DotNet.Build.Tasks
{
    public class GenerateSdkRuntimeIdentifierChain : Task
    {
        [Required]
        public string RuntimeIdentifier { get; set; }

        [Required]
        public string RuntimeIdentifierGraphPath { get; set; }

        [Required]
        public string RuntimeIdentifierChainOutputPath { get; set; }

        public override bool Execute()
        {
            var runtimeIdentifierGraph = JsonRuntimeFormat.ReadRuntimeGraph(RuntimeIdentifierGraphPath);

            var chainContents = string.Join("\n", runtimeIdentifierGraph.ExpandRuntime(RuntimeIdentifier));
            if (!File.Exists(RuntimeIdentifierChainOutputPath) ||
                !File.ReadAllBytes(RuntimeIdentifierChainOutputPath).AsSpan().SequenceEqual(Encoding.UTF8.GetBytes(chainContents)))
            {
                IncrementalLayoutState.WriteFile(RuntimeIdentifierChainOutputPath, chainContents);
            }

            return true;
        }
    }
}
