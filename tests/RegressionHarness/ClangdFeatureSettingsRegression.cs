using System;
using System.IO;
using System.Linq;
using System.Text;
using ClangdVS;

internal static class ClangdFeatureSettingsRegression
{
    internal static void Run()
    {
        var settings = new ClangdSettings();
        var expected = new[]
        {
            Tuple.Create("EnableCompletionAndHover", true),
            Tuple.Create("EnableDiagnostics", true),
            Tuple.Create("EnableSemanticHighlighting", true),
            Tuple.Create("EnableFormatting", true),
            Tuple.Create("EnableCodeActions", true),
            Tuple.Create("EnableNavigation", true),
            Tuple.Create("EnableInactiveRegions", true),
            Tuple.Create("EnableInlayHints", true),
            Tuple.Create("EnableAstView", false),
            Tuple.Create("EnableSymbolInfo", false),
            Tuple.Create("EnableTypeHierarchy", false),
        };

        foreach (var entry in expected)
        {
            var property = typeof(ClangdSettings).GetProperty(entry.Item1);
            Assert(property != null && property.PropertyType == typeof(bool),
                   $"missing boolean setting {entry.Item1}");
            Assert((bool)property.GetValue(settings) == entry.Item2,
                   $"setting {entry.Item1} does not use its compatible default");
            var option = typeof(ClangdOptionsPage).GetProperty(entry.Item1);
            Assert(option != null, $"options page is missing {entry.Item1}");
            var optionPage = (ClangdOptionsPage)System.Runtime.Serialization.FormatterServices
                                                    .GetUninitializedObject(typeof(ClangdOptionsPage));
            var singletonValue = property.GetValue(ClangdSettings.Instance);
            option.SetValue(optionPage, !entry.Item2);
            Assert((bool)property.GetValue(ClangdSettings.Instance) == !entry.Item2,
                   $"options page did not write setting {entry.Item1}");
            property.SetValue(ClangdSettings.Instance, singletonValue);
        }

        var pageProperties = typeof(ClangdOptionsPage).GetProperties()
                                                      .Where(property => property.PropertyType == typeof(bool))
                                                      .Select(property => property.Name)
                                                      .ToArray();
        foreach (var entry in expected)
            Assert(pageProperties.Contains(entry.Item1), $"options page does not expose {entry.Item1}");

        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(
                   "{\"ClangdPath\":\"clangd\",\"TakeOverFromNativeIntelliSense\":true}")))
        {
            var serializer = new System.Runtime.Serialization.Json.DataContractJsonSerializer(typeof(ClangdSettings));
            var legacy = (ClangdSettings)serializer.ReadObject(stream);
            foreach (var entry in expected)
            {
                var property = typeof(ClangdSettings).GetProperty(entry.Item1);
                Assert((bool)property.GetValue(legacy) == entry.Item2,
                       $"legacy settings did not retain the default for {entry.Item1}");
            }
        }

        ClangdSettingsReloadRegression.Run();
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
