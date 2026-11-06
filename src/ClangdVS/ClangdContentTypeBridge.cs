// ClangdContentTypeBridge.cs
//
// Bridges the content type "cpp-clangd": a dual-base-class design (mechanism proven by decompiling the VS 18.9 LSP host) --
//   base "C/C++"     => all native VC editor components with [ContentType("C/C++")] (syntax coloring,
//                     brace matching, etc. match along the inheritance chain) keep working;
//   base "code-languageserver-preview" (= CodeRemoteContentTypeName) => enters the LSP
//                     document pipeline (DocumentOperationListener only dispatches
//                     didOpen for content types derived from languageserver-base).
//
// Note: we deliberately no longer statically map C/C++ file extensions here. Live evidence (2026-10-16) proves:
// static mapping makes the buffer born as cpp-clangd, while the native VC language service is only attached at creation,
// so after takeover is disabled even switching back to C/C++ will not reattach it, and native completion/hover stay permanently Dismissed.
// The buffer is therefore always born native; only while takeover is enabled does TakeoverContentTypeRouter
// dynamically switch it to cpp-clangd (already-open files via a switch, new documents via package scanning), and it no longer hijacks when disabled.

using System.ComponentModel.Composition;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.Utilities;

namespace ClangdVS
{
    internal static class ClangdContentTypeBridge
    {
        // MEF content type definition: multiple base classes are legal (BaseDefinitionAttribute is a MultipleBaseMetadataAttribute).
        [Export]
        [Name("cpp-clangd")]
        [BaseDefinition("C/C++")] // keeps native C/C++ editor features
        [BaseDefinition(
            CodeRemoteContentDefinition.CodeRemoteContentTypeName)] // "code-languageserver-preview" enters the LSP pipeline
        internal static ContentTypeDefinition CppClangdContentTypeDefinition;
    }
}
