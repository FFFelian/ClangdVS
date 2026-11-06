using System;
using System.Reflection;
using System.Threading;
using ClangdVS;

internal static class ClangdExtensionRequestsRegression
{
    internal static void Run()
    {
        var client = new ClangdLanguageClient();
        AssertNull(client, "RequestAstAsync",
                   new object[] { "file:///C:/Work/a.cpp", 1, 2, CancellationToken.None });
        AssertNull(client, "RequestSymbolInfoAsync",
                   new object[] { "file:///C:/Work/a.cpp", 1, 2, CancellationToken.None });
        AssertNull(client, "RequestTypeHierarchyAsync",
                   new object[] { "file:///C:/Work/a.cpp", 1, 2, CancellationToken.None });
    }

    private static void AssertNull(ClangdLanguageClient client, string name, object[] args)
    {
        var method = typeof(ClangdLanguageClient).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);
        if (method == null)
            throw new InvalidOperationException("missing clangd extension request method " + name);
        var task = method.Invoke(client, args) as System.Threading.Tasks.Task;
        if (task == null)
            throw new InvalidOperationException(name + " did not return a task");
        task.GetAwaiter().GetResult();
        var result = task.GetType().GetProperty("Result")?.GetValue(task);
        if (result != null)
            throw new InvalidOperationException(name + " should not send a request when its feature defaults off");
    }
}
