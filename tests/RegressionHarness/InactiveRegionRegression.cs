using System;
using System.Reflection;
using ClangdVS;

internal static class InactiveRegionRegression
{
    internal static void Run()
    {
        var type = typeof(ClangdVSPackage).Assembly.GetType("ClangdVS.InactiveRegionStore");
        Assert(type != null, "inactive-region store is missing");
        var store = Activator.CreateInstance(type, true);
        var update = type.GetMethod("Update", BindingFlags.Instance | BindingFlags.NonPublic);
        var get = type.GetMethod("Get", BindingFlags.Instance | BindingFlags.NonPublic);
        var remove = type.GetMethod("Remove", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert(update != null && get != null && remove != null, "inactive-region store API is incomplete");

        var uri = new Uri("file:///C:/Work/a.cpp");
        update.Invoke(store, new object[] { uri, new[] { new Tuple<int, int, int, int>(1, 2, 3, 4) } });
        update.Invoke(store, new object[] { uri, new Tuple<int, int, int, int>[0] });
        var empty = (System.Collections.ICollection)get.Invoke(store, new object[] { uri });
        Assert(empty.Count == 0, "empty inactive-region update failed to retire previous ranges");
        update.Invoke(store, new object[] { uri, new[] { Tuple.Create(0, 0, 0, 5) } });
        Assert(empty.Count == 0, "store exposed mutable ranges from an earlier snapshot");
        var populated = (System.Collections.ICollection)get.Invoke(store, new object[] { uri });
        Assert(populated.Count == 1, "inactive-region store did not retain the latest range set");
        remove.Invoke(store, new object[] { uri });
        Assert(((System.Collections.ICollection)get.Invoke(store, new object[] { uri })).Count == 0,
               "inactive-region store did not release a closed document");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
