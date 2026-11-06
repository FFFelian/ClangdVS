using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.LanguageServer.Client;
using Microsoft.VisualStudio.RpcContracts.DiagnosticManagement;
using Microsoft.VisualStudio.Threading;

namespace ClangdVS
{
    internal sealed class TakeoverDiagnosticSource
    {
        internal TakeoverDiagnosticSource(string name, bool remoteOnly)
        {
            Name = name;
            RemoteOnly = remoteOnly;
        }

        internal string Name { get; }

        // The VS LSP host sends UX-disabled clients to the broadcast manager.
        internal bool RemoteOnly { get; }

        internal static TakeoverDiagnosticSource FromClient(ILanguageClient client)
        {
            if (client == null)
                throw new ArgumentNullException(nameof(client));
            var disabled = client.GetType()
                                 .GetCustomAttributes(typeof(DisableUserExperienceAttribute), true)
                                 .OfType<DisableUserExperienceAttribute>()
                                 .FirstOrDefault();
            return new TakeoverDiagnosticSource(client.Name, disabled?.DisableUserExperience == true);
        }
    }

    internal static class TakeoverDiagnosticCleanup
    {
        internal static async Task<int> ClearRetiredProviderAsync(
            bool takeoverEnabled,
            IEnumerable<TakeoverDiagnosticSource> nativeSources,
            TakeoverDiagnosticSource clangdSource,
            Func<CancellationToken, Task<IReadOnlyList<DiagnosticReport>>> getReports,
            Func<bool, string, CancellationToken, Task> clearGenerator,
            CancellationToken cancellationToken)
        {
            var retired = takeoverEnabled
                              ? nativeSources ?? Enumerable.Empty<TakeoverDiagnosticSource>()
                              : new[] { clangdSource };
            var sources = retired.Where(source => source != null && !string.IsNullOrEmpty(source.Name)).ToArray();
            if (sources.Length == 0)
                return 0;

            var reports = await getReports(cancellationToken).ConfigureAwait(false) ??
                          Array.Empty<DiagnosticReport>();
            var cleared = 0;
            List<Exception> failures = null;
            foreach (var source in sources)
            {
                var generatorIds = reports.Where(report => report?.GeneratorId != null &&
                                                            report.DiagnosticCollections?.Count > 0 &&
                                                            MatchesSource(report.GeneratorId, source.Name))
                                           .Select(report => report.GeneratorId)
                                           .Distinct(StringComparer.Ordinal)
                                           .ToArray();
                foreach (var generatorId in generatorIds)
                {
                    try
                    {
                        await clearGenerator(source.RemoteOnly, generatorId, cancellationToken).ConfigureAwait(false);
                        cleared++;
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        if (failures == null)
                            failures = new List<Exception>();
                        failures.Add(ex);
                    }
                }
            }

            if (failures != null)
                throw new AggregateException("One or more retired diagnostic generators could not be cleared.",
                                             failures);

            return cleared;
        }

        private static bool MatchesSource(string generatorId, string clientName) =>
            generatorId.EndsWith(clientName, StringComparison.Ordinal);
    }

    /// <summary>Bridges the cleanup policy to VS's cached diagnostic proxies.</summary>
    internal static class VsTakeoverDiagnostics
    {
        private const string NativeClientAssembly = "Microsoft.VisualStudio.VC.LanguageServer";
        private const string FactoryTypeName =
            "Microsoft.VisualStudio.LanguageServer.Client.IDiagnosticManagerServiceFactory";

        internal static async Task<bool> ClearRetiredProviderAsync(IComponentModel componentModel,
                                                                   bool takeoverEnabled,
                                                                   CancellationToken cancellationToken)
        {
            try
            {
                var clients = componentModel.DefaultExportProvider.GetExportedValues<ILanguageClient>().ToArray();
                var nativeSources = clients.Where(client => string.Equals(
                                                       client.GetType().Assembly.GetName().Name,
                                                       NativeClientAssembly,
                                                       StringComparison.Ordinal))
                                            .Select(TakeoverDiagnosticSource.FromClient)
                                            .ToArray();
                var clangdClient =
                    componentModel.DefaultExportProvider.GetExportedValueOrDefault<ClangdLanguageClient>();
                var clangdSource = clangdClient == null
                                       ? null
                                       : TakeoverDiagnosticSource.FromClient(clangdClient);
                object factory = null;
                var cleared = await TakeoverDiagnosticCleanup.ClearRetiredProviderAsync(
                                  takeoverEnabled,
                                  nativeSources,
                                  clangdSource,
                                  async token =>
                                  {
                                      factory = factory ?? ResolveFactory(componentModel);
                                      return await ReadReportsAsync(factory, token).ConfigureAwait(false);
                                  },
                                  async (remoteOnly, generatorId, token) =>
                                  {
                                      factory = factory ?? ResolveFactory(componentModel);
                                      await ClearGeneratorAsync(factory, remoteOnly, generatorId, token)
                                          .ConfigureAwait(false);
                                  },
                                  cancellationToken).ConfigureAwait(false);
                ClangdLog.WriteLine(
                    $"takeover: retired diagnostic cleanup enabled={takeoverEnabled} cleared={cleared}.");
                return true;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine(
                    $"takeover: retired diagnostic cleanup failed enabled={takeoverEnabled}: {ex}");
                return false;
            }
        }

        internal static Task<bool> ClearClangdDiagnosticsAsync(IComponentModel componentModel,
                                                               CancellationToken cancellationToken)
        {
            if (componentModel == null)
                return Task.FromResult(false);
            var client = componentModel.DefaultExportProvider.GetExportedValueOrDefault<ClangdLanguageClient>();
            if (client == null)
                return Task.FromResult(true);
            return ClearSourceAsync(componentModel, TakeoverDiagnosticSource.FromClient(client), cancellationToken);
        }

        private static async Task<bool> ClearSourceAsync(IComponentModel componentModel,
                                                          TakeoverDiagnosticSource source,
                                                          CancellationToken cancellationToken)
        {
            try
            {
                object factory = null;
                var cleared = await TakeoverDiagnosticCleanup.ClearRetiredProviderAsync(
                                  false,
                                  Array.Empty<TakeoverDiagnosticSource>(),
                                  source,
                                  async token =>
                                  {
                                      factory = factory ?? ResolveFactory(componentModel);
                                      return await ReadReportsAsync(factory, token).ConfigureAwait(false);
                                  },
                                  async (remoteOnly, generatorId, token) =>
                                  {
                                      factory = factory ?? ResolveFactory(componentModel);
                                      await ClearGeneratorAsync(factory, remoteOnly, generatorId, token)
                                          .ConfigureAwait(false);
                                  },
                                  cancellationToken).ConfigureAwait(false);
                ClangdLog.WriteLine($"diagnostics: clangd reports cleared={cleared}.");
                return true;
            }
            catch (Exception ex)
            {
                ClangdLog.WriteLine($"diagnostics: failed to clear clangd reports: {ex}");
                return false;
            }
        }

        private static object ResolveFactory(IComponentModel componentModel)
        {
            var factoryType = AppDomain.CurrentDomain.GetAssemblies()
                                       .Select(assembly => assembly.GetType(FactoryTypeName, false))
                                       .FirstOrDefault(type => type != null);
            if (factoryType == null)
                throw new InvalidOperationException("VS diagnostic manager service factory is unavailable.");
            var export = componentModel.DefaultExportProvider
                                       .GetExports(factoryType, null, FactoryTypeName)
                                       .FirstOrDefault();
            if (export?.Value == null)
                throw new InvalidOperationException("VS diagnostic manager service factory is not exported.");
            return export.Value;
        }

        private static async Task<IReadOnlyList<DiagnosticReport>> ReadReportsAsync(
            object factory, CancellationToken cancellationToken)
        {
            var viewer = await GetCachedProxyAsync<IDiagnosticViewerService>(
                             factory, "DiagnosticViewerService", cancellationToken).ConfigureAwait(false);
            var collector = new DiagnosticReportCollector();
            await viewer.RequestDiagnosticsAsync(collector, cancellationToken).ConfigureAwait(false);
            return collector.Reports;
        }

        private static async Task ClearGeneratorAsync(object factory, bool remoteOnly, string generatorId,
                                                       CancellationToken cancellationToken)
        {
            var propertyName = remoteOnly ? "RemoteOnlyDiagnosticManagerService" : "DiagnosticManagerService";
            var manager = await GetCachedProxyAsync<IDiagnosticManagerService>(
                              factory, propertyName, cancellationToken).ConfigureAwait(false);
            await manager.ClearDiagnosticsAsync(generatorId, cancellationToken).ConfigureAwait(false);
        }

        private static async Task<T> GetCachedProxyAsync<T>(object factory, string propertyName,
                                                            CancellationToken cancellationToken) where T : class
        {
            var property = factory.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
            if (!(property?.GetValue(factory) is AsyncLazy<T> lazy))
                throw new InvalidOperationException($"VS diagnostic proxy {propertyName} is unavailable.");
            var proxy = await lazy.GetValueAsync(cancellationToken).ConfigureAwait(false);
            if (proxy == null)
                throw new InvalidOperationException($"VS diagnostic proxy {propertyName} could not be acquired.");
            return proxy;
        }

        private sealed class DiagnosticReportCollector : IProgress<DiagnosticReport>
        {
            private readonly List<DiagnosticReport> _reports = new List<DiagnosticReport>();

            internal IReadOnlyList<DiagnosticReport> Reports
            {
                get
                {
                    lock (_reports)
                        return _reports.ToArray();
                }
            }

            public void Report(DiagnosticReport value)
            {
                if (value == null)
                    return;
                lock (_reports)
                    _reports.Add(value);
            }
        }
    }
}
