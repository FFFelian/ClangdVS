//-----------------------------------------------------------------------------
// clangd for Visual Studio - command table symbols.
//
// These live in a CTC header rather than the <Symbols> section of ClangdVS.vsct;
// VSCTLibrary resolves them through the <Extern> directive (semantically
// identical, and the extern-header path is the proven one).

#define guidClangdVSPkg { 0x7e1b3a54, 0x64c1, 0x4f0b, { 0x9a, 0x2d, 0x52, 0xc4, 0xf1, 0xc6, 0xc9, 0x99 } }
#define guidClangdVSCmdSet { 0x9a2c5e77, 0x3d44, 0x4e5f, { 0x8b, 0x6a, 0x0c, 0x1d, 0x2e, 0x3f, 0x4a, 0x55 } }

#define ClangdMenuGroup 0x1020
#define ClangdEditorContextGroup 0x1021
#define ClangdMenuItemGroup 0x1022
#define ClangdMenu 0x1030
#define cmdidSwitchHeaderSource 0x1100
#define cmdidRestartClangd 0x1101
#define cmdidToggleTakeover 0x1102
#define cmdidSwitchHeaderSourceContext 0x1103
#define cmdidViewClangdAst 0x1104
#define cmdidViewClangdSymbolInfo 0x1105
#define cmdidViewClangdTypeHierarchy 0x1106
