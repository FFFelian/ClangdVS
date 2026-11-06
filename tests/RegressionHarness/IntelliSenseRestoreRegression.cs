using System;
using System.Collections.Generic;
using System.Reflection;
using ClangdVS;
using Microsoft.Win32;
using Moq;

internal static class IntelliSenseRestoreRegression
{
    internal static System.Threading.Tasks.Task RunAsync()
    {
        var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.IntelliSenseSuppression");
        var method = type?.GetMethod("RestoreProperty", BindingFlags.Static | BindingFlags.NonPublic);
        Assert(method != null, "backup-aware restore policy is missing");
        var backup =
            new Dictionary<string, string> { ["dte:DisableIntelliSense"] = "True", ["dte:ErrorTagType"] = "2" };
        var savedBoolean = Property("DisableIntelliSense", false);
        Restore(method, backup, "dte:DisableIntelliSense", savedBoolean.Object);
        savedBoolean.VerifySet(p => p.Value = true, Times.Once);
        var unchangedBoolean = Property("EnableLinter", false);
        Restore(method, backup, "dte:EnableLinter", unchangedBoolean.Object);
        unchangedBoolean.VerifySet(p => p.Value = It.IsAny<object>(), Times.Never);
        var savedInteger = Property("ErrorTagType", 0);
        Restore(method, backup, "dte:ErrorTagType", savedInteger.Object);
        savedInteger.VerifySet(p => p.Value = 2, Times.Once);
        var unchangedInteger = Property("OtherSeverity", 0);
        Restore(method, backup, "dte:OtherSeverity", unchangedInteger.Object);
        unchangedInteger.VerifySet(p => p.Value = It.IsAny<object>(), Times.Never);
        RegistryFallbackRestoresExistingNonzeroValue(type);
        Console.WriteLine("PASS: registry fallback restores existing nonzero DWORD values");
        RegistryFallbackPreservesExistingNonDwordValue(type);
        Console.WriteLine("PASS: registry fallback preserves existing non-DWORD values and types");
        RegistryFallbackPreservesAlreadyActiveValue(type);
        Console.WriteLine("PASS: registry fallback leaves already-active DWORD values unchanged");
        RegistryFallbackRemovesOnlyValuesCreatedByTakeover(type);
        Console.WriteLine("PASS: registry fallback removes only takeover-created flags and empty keys");
        return System.Threading.Tasks.Task.CompletedTask;
    }

    private static void RegistryFallbackPreservesExistingNonDwordValue(Type type)
    {
        var root = TestRegistryRoot();
        try
        {
            var keyPath = root + @"\Text Editor\C/C++-Specific";
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                key.SetValue("DisableIntelliSense", "user-value", RegistryValueKind.String);
            SetSuppressed(type, true, root);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert((string)key.GetValue("DisableIntelliSense") == "user-value",
                       "takeover overwrote a pre-existing non-DWORD value");
                Assert(key.GetValueKind("DisableIntelliSense") == RegistryValueKind.String,
                       "takeover changed the type of a pre-existing registry value");
            }
            SetSuppressed(type, false, root);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert((string)key.GetValue("DisableIntelliSense") == "user-value",
                       "revert failed to preserve a pre-existing non-DWORD value");
                Assert(key.GetValueKind("DisableIntelliSense") == RegistryValueKind.String,
                       "revert failed to preserve the original registry value type");
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, false);
        }
    }

    private static void RegistryFallbackPreservesAlreadyActiveValue(Type type)
    {
        var root = TestRegistryRoot();
        try
        {
            var keyPath = root + @"\Text Editor\C/C++-Specific";
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
                key.SetValue("DisableIntelliSense", 1, RegistryValueKind.DWord);
            SetSuppressed(type, true, root);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert((int)key.GetValue("DisableIntelliSense") == 1,
                       "revert changed an already-active DWORD value that takeover did not modify");
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, false);
        }
    }

    private static void RegistryFallbackRestoresExistingNonzeroValue(Type type)
    {
        var root = TestRegistryRoot();
        try
        {
            var keyPath = root + @"\Text Editor\C/C++-Specific";
            using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
            {
                key.SetValue("DisableIntelliSense", 2, RegistryValueKind.DWord);
            }
            SetSuppressed(type, true, root);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
                Assert((int)key.GetValue("DisableIntelliSense") == 1, "registry takeover did not set the flag");
            SetSuppressed(type, false, root);
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert((int)key.GetValue("DisableIntelliSense") == 2,
                       "registry fallback did not restore the existing nonzero value");
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(root, false);
        }
    }

    private static void RegistryFallbackRemovesOnlyValuesCreatedByTakeover(Type type)
    {
        var existingRoot = TestRegistryRoot();
        var missingRoot = TestRegistryRoot();
        try
        {
            var existingKeyPath = existingRoot + @"\Text Editor\C/C++-Specific";
            using (var key = Registry.CurrentUser.CreateSubKey(existingKeyPath))
                key.SetValue("Unrelated", "preserve", RegistryValueKind.String);
            SetSuppressed(type, true, existingRoot);
            SetSuppressed(type, false, existingRoot);
            using (var key = Registry.CurrentUser.OpenSubKey(existingKeyPath))
            {
                Assert(key != null, "revert deleted a registry key that existed before takeover");
                Assert(key.GetValue("DisableIntelliSense") == null,
                       "revert left a registry value that takeover created");
                Assert((string)key.GetValue("Unrelated") == "preserve",
                       "revert removed an unrelated value from the registry key");
            }

            var missingKeyPath = missingRoot + @"\Text Editor\C/C++-Specific";
            SetSuppressed(type, true, missingRoot);
            SetSuppressed(type, false, missingRoot);
            Assert(Registry.CurrentUser.OpenSubKey(missingKeyPath) == null,
                   "revert left the key created solely by takeover");
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(existingRoot, false);
            Registry.CurrentUser.DeleteSubKeyTree(missingRoot, false);
        }
    }

    private static string TestRegistryRoot() => @"Software\ClangdVS-RegressionHarness\" + Guid.NewGuid().ToString("N");

    private static void SetSuppressed(Type type, bool enabled, string root)
    {
        type.GetMethod("SetSuppressed", BindingFlags.Static | BindingFlags.Public)
            .Invoke(null, new object[] { enabled, null, root });
    }

    private static Mock<EnvDTE.Property> Property(string name, object value)
    {
        var property = new Mock<EnvDTE.Property>(MockBehavior.Strict);
        property.SetupGet(p => p.Name).Returns(name);
        property.SetupGet(p => p.Value).Returns(value);
        property.SetupSet(p => p.Value = It.IsAny<object>());
        return property;
    }

    private static void Restore(MethodInfo method, Dictionary<string, string> backup, string key,
                                EnvDTE.Property property)
    {
        method.Invoke(null, new object[] { backup, key, property });
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
