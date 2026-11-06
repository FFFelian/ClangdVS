using System;
using System.Collections.Generic;
using System.IO;

namespace ClangdVS
{
    /// <summary>
    /// The single definition of C/C++ file extensions. The content-type bridge, command enablement checks,
    /// and file watching must all reference this one; keeping a private copy in each place is forbidden.
    /// </summary>
    internal static class ClangdFileExtensions
    {
        public static readonly string[] Extensions = {
            ".c", ".cc", ".cpp", ".cxx", ".c++", ".h", ".hh", ".hpp", ".hxx", ".inl", ".ipp", ".ixx", ".cppm", ".tpp",
        };

        private static readonly HashSet<string> Lookup =
            new HashSet<string>(Extensions, StringComparer.OrdinalIgnoreCase);

        public static bool IsCppFile(string path)
        {
            var extension = Path.GetExtension(path);
            return !string.IsNullOrEmpty(extension) && Lookup.Contains(extension);
        }
    }
}
