using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;

namespace ClangdVS
{
    /// <summary>
    /// Priority command target: takes over Edit.GoToDefinition ahead of the native C/C++ handler.
    ///
    /// This target takes over only when takeover is enabled and the active document is C++ (querying
    /// clangd over the direct channel and navigating with DTE); everything else returns NOTFOUND to pass
    /// through, leaving native behavior unaffected.
    /// </summary>
    internal sealed class ClangdNavigationTarget : IOleCommandTarget
    {
        // SDK standard command set and IDs, to avoid hand-written GUIDs/numbers deviating from the actual command routing.
        internal static readonly Guid CommandSet = VSConstants.GUID_VSStandardCommandSet97;

        internal const uint CmdGotoDefn = (uint)VSConstants.VSStd97CmdID.GotoDefn;

        // OLECMDERR_E_NOTFOUND: this target does not handle it, so routing continues onward.
        internal const int NotHandled = unchecked((int)0x80040104);

        private readonly ClangdVSPackage _package;

        public ClangdNavigationTarget(ClangdVSPackage package)
        {
            _package = package;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            return QueryNavigationStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText, _package.ShouldHandleNavigation,
                                         null);
        }

        internal static int QueryNavigationStatus(ref Guid group, uint count, OLECMD[] commands, IntPtr commandText,
                                                  Func<bool> shouldHandle, IOleCommandTarget next)
        {
            if (commands == null)
            {
                return unchecked((int)0x80004003); // E_POINTER
            }

            if (count > commands.Length)
            {
                return VSConstants.E_INVALIDARG;
            }

            var hasNavigation = false;
            var hasOtherCommands = false;
            if (group == CommandSet)
            {
                for (var i = 0; i < count; i++)
                {
                    if (commands[i].cmdID == CmdGotoDefn)
                        hasNavigation = true;
                    else
                        hasOtherCommands = true;
                }
            }

            // A shell priority target has no downstream target. Leave mixed
            // batches untouched so the view chain can answer the entire batch.
            if (next == null && hasOtherCommands)
            {
                return NotHandled;
            }

            var handleNavigation = hasNavigation && shouldHandle();
            var nextHr = next?.QueryStatus(ref group, count, commands, commandText) ?? NotHandled;
            if (!handleNavigation)
            {
                return nextHr;
            }

            for (var i = 0; i < count; i++)
            {
                if (commands[i].cmdID == CmdGotoDefn)
                    commands[i].cmdf = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_ENABLED);
            }

            return VSConstants.S_OK;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            return _package.ExecGotoCommand(ref pguidCmdGroup, nCmdID, CommandSet, CmdGotoDefn);
        }
    }

    /// <summary>
    /// View-level go-to-definition filter: explicitly attached by the package side via EnumViews (see
    /// ClangdVSPackage.EnsureViewFiltersAsync); it shares the package methods with the shell target.
    /// Routing stops at S_OK and does not process the command twice.
    /// </summary>
    internal sealed class ClangdViewCommandFilter : IOleCommandTarget
    {
        private readonly ClangdVSPackage _package;

        private IOleCommandTarget _nextTarget;

        public ClangdViewCommandFilter(ClangdVSPackage package)
        {
            _package = package;
        }

        internal void SetNextTarget(IOleCommandTarget nextTarget)
        {
            _nextTarget = nextTarget;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            return ClangdNavigationTarget.QueryNavigationStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText,
                                                                _package.ShouldHandleNavigation, _nextTarget);
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            var hr = _package.ExecGotoCommand(ref pguidCmdGroup, nCmdID, ClangdNavigationTarget.CommandSet,
                                              ClangdNavigationTarget.CmdGotoDefn);
            if (hr != ClangdNavigationTarget.NotHandled || _nextTarget == null)
            {
                return hr;
            }

            return _nextTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }
    }
}
