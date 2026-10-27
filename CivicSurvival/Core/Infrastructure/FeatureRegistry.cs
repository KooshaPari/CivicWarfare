using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CivicSurvival.Core.Attributes;
using Game;
using Unity.Entities;
using CivicSurvival.Core.Interfaces.Core;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Core.Infrastructure
{
    public enum FeatureUnavailableReason
    {
        None = 0,
        Closed,
        DependencySkipped,
        Failed,

        /// <summary>
        /// Configured-open but the feature's rollout wave has not been reached
        /// yet. The systems are intentionally not registered; the UI shows a
        /// dimmed preview. Distinct from <see cref="Closed"/> (not shipped at
        /// all) so callers can present "coming in wave N" vs "unavailable".
        /// </summary>
        WaveLocked
    }

    /// <summary>
    /// Composition root and runtime query API for gameplay features.
    /// TIER 0: Determines WHICH systems to create at startup AND provides
    /// the only legal cross-feature system access (Query&lt;T&gt;()).
    ///
    /// Lifecycle:
    ///   1. FeatureRegistry.Initialize() in Mod.OnLoad()
    ///   2. Register features via Register(feature)
    ///   3. RegisterOpenFeatures(updateSystem, manifest) called from SystemRegistrar
    ///      a. Evaluate IGatedFeatureModule.Gate against manifest
    ///      b. Evaluate IDependentFeatureModule.Dependencies (closed dep -> closed dependent)
    ///      c. For each open feature: RegisterContent -> RegisterSystems -> RegisterUI
    ///   4. Query&lt;T&gt;() for cross-feature access at runtime
    ///   5. Dispose() in Mod.OnDispose()
    ///
    /// Thread-safe via lock for registration operations.
    /// </summary>
    public sealed class FeatureRegistry : IDisposable
    {
        private static readonly LogContext Log = new("FeatureRegistry");

        private static FeatureRegistry s_Instance = null!;
        private static readonly object s_Lock = new();

        public static FeatureRegistry Instance
        {
            get
            {
                lock (s_Lock)
                {
                    return s_Instance
                        ?? throw new InvalidOperationException("FeatureRegistry not initialized. Call Initialize() in Mod.OnLoad().");
                }
            }
        }

        public static bool IsInitialized
        {
            get { lock (s_Lock) return s_Instance != null; }
        }

        private readonly Dictionary<string, IFeatureModule> m_Features = new(StringComparer.Ordinal);
        private List<IFeatureModule> m_SortedFeatures = null!;
        private readonly HashSet<string> m_OpenFeatures = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_ClosedFeatures = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_PreviewFeatures = new(StringComparer.Ordinal);
        private readonly HashSet<string> m_DepSkippedFeatures = new(StringComparer.Ordinal);
        private readonly List<string> m_FailedFeatures = new();
#pragma warning disable CA2213 // Borrowed Unity World reference; lifecycle is owned by the game engine, not FeatureRegistry.
        private World m_World = null!;
#pragma warning restore CA2213
        private FeatureManifest m_Manifest = null!;
        private bool m_RegistrationComplete;
        private bool m_Disposed;

        public IReadOnlyList<string> FailedFeatures => m_FailedFeatures;
        public IReadOnlyCollection<string> OpenFeatureIds => m_OpenFeatures;
        public IReadOnlyCollection<string> ClosedFeatureIds => m_ClosedFeatures;
        public IReadOnlyCollection<string> PreviewFeatureIds => m_PreviewFeatures;
        public IReadOnlyCollection<string> DepSkippedFeatureIds => m_DepSkippedFeatures;

        public IReadOnlyCollection<string> GetRegisteredFeatureIds()
        {
            lock (s_Lock) return m_Features.Keys.ToList();
        }

        /// <summary>
        /// True if a feature module with this id was registered (regardless of
        /// open/closed/dep-skipped/failed state). Distinguishes legitimate
        /// "feature is closed" from "FeatureId typo or stale reference".
        /// </summary>
        public bool IsKnownFeatureId(string featureId)
        {
            if (string.IsNullOrEmpty(featureId)) return false;
            lock (s_Lock) return m_Features.ContainsKey(featureId);
        }

        /// <summary>
        /// Snapshot of the manifest used at registration time. Null until
        /// <see cref="RegisterOpenFeatures"/> runs.
        /// </summary>
        public FeatureManifest ActiveManifest => m_Manifest;

        private FeatureRegistry() { }

        public static void Initialize()
        {
            List<string> leakedNames = null!;
            int leakedCount = 0;
            lock (s_Lock)
            {
                if (s_Instance != null)
                {
                    leakedCount = s_Instance.m_Features.Count;
                    leakedNames = s_Instance.m_Features.Values.Select(f => f.Name).ToList();
                }
                else
                {
                    s_Instance = new FeatureRegistry();
                }
            }
            if (leakedNames != null)
            {
                Log.Error($"ALREADY EXISTS with {leakedCount} features! Previous instance not disposed properly.");
                foreach (var name in leakedNames)
                    Log.Error($"Leaked feature: {name}");
                throw new InvalidOperationException(
                    $"FeatureRegistry already initialized with {leakedCount} features. Call Dispose() before re-initializing.");
            }
            Log.Info("Initialized");
        }

        public void Register(IFeatureModule feature)
        {
            if (feature == null)
                throw new ArgumentNullException(nameof(feature));

            lock (s_Lock)
            {
                if (m_Disposed)
                    throw new ObjectDisposedException(nameof(FeatureRegistry));

                if (m_Features.ContainsKey(feature.Name))
                    throw new InvalidOperationException($"Feature already registered: {feature.Name}");

                m_Features[feature.Name] = feature;
                m_FailedFeatures.Remove(feature.Name);
                m_SortedFeatures = null!;
            }
            Log.Info($"Registered: {feature.Name} (priority {feature.Priority})");
        }

        public int Count
        {
            get { lock (s_Lock) return m_Disposed ? 0 : m_Features.Count; }
        }

        /// <summary>
        /// Register systems / content / UI for every open feature.
        /// Open = wave reached, gate evaluates to true, and no required dependency is unavailable.
        /// Lifecycle order per feature: RegisterContent -> RegisterSystems -> RegisterUI.
        /// </summary>
        public void RegisterOpenFeatures(UpdateSystem updateSystem, FeatureManifest manifest)
        {
            if (updateSystem == null)
                throw new ArgumentNullException(nameof(updateSystem));
            if (manifest == null)
                throw new ArgumentNullException(nameof(manifest));

            IReadOnlyList<IFeatureModule> sorted;
            lock (s_Lock)
            {
                if (m_Disposed) return;
                m_World = updateSystem.World;
                m_Manifest = manifest;
                m_RegistrationComplete = false;
                m_OpenFeatures.Clear();
                m_ClosedFeatures.Clear();
                m_PreviewFeatures.Clear();
                m_DepSkippedFeatures.Clear();
                m_FailedFeatures.Clear();
                sorted = GetSortedFeatures();
            }

            FeatureGraphValidator.Validate(sorted);
            WarnUnknownManifestEntries(sorted, manifest);
            ValidateOwnedByFeatureIds();

            Log.Info("════════════════════════════════════════");
            Log.Info($"Registering features: {sorted.Count} candidates");

            var ctx = new GateContext(this);
            int failures = 0;

            foreach (var feature in sorted)
            {
                var wave = manifest.WaveOf(feature.Name);
                if (wave >= FeatureWaveConstants.WAVE_SENTINEL_UNAVAILABLE)
                {
                    lock (s_Lock) m_ClosedFeatures.Add(feature.Name);
                    Log.Info($"  closed: {feature.Name} (wave >= sentinel)");
                    Log.Info($"[FEATURE-VERIFY] feature={feature.Name} state=closed reason=Closed wave={wave} currentWave={manifest.CurrentWave} registration=skipped");
                    continue;
                }

                // Future wave below sentinel -> preview. Systems are intentionally
                // NOT registered. The UI MUST query IsUnavailable(featureId, out
                // reason) and render an honest "locked" affordance (reason=WaveLocked);
                // it MUST NOT fabricate preview data, dimmed mock-ups, or fake
                // placeholder rows from DTO defaults. The existing feature-flag
                // mechanism (m_PreviewFeatures + WaveLocked) is the only legal way
                // for callers to detect this state.
                if (wave > manifest.CurrentWave)
                {
                    lock (s_Lock) m_PreviewFeatures.Add(feature.Name);
                    Log.Info($"  preview: {feature.Name} (wave {wave}, current {manifest.CurrentWave})");
                    Log.Info($"[FEATURE-VERIFY] feature={feature.Name} state=preview reason=WaveLocked wave={wave} currentWave={manifest.CurrentWave} registration=skipped");
                    continue;
                }

                if (HasUnavailableDependency(feature, out var blocker, out var blockerReason))
                {
                    lock (s_Lock) m_DepSkippedFeatures.Add(feature.Name);
                    Log.Info($"  dep-skipped: {feature.Name} (requires unavailable: {blocker}, reason={blockerReason})");
                    Log.Info($"[FEATURE-VERIFY] feature={feature.Name} state=dep-skipped reason=DependencySkipped blocker={blocker} blockerReason={blockerReason} registration=skipped");
                    continue;
                }

                if (HasUnavailableGateDependency(feature, out blocker, out blockerReason))
                {
                    lock (s_Lock) m_DepSkippedFeatures.Add(feature.Name);
                    Log.Info($"  dep-skipped: {feature.Name} (gate requires unavailable: {blocker}, reason={blockerReason})");
                    Log.Info($"[FEATURE-VERIFY] feature={feature.Name} state=dep-skipped reason=DependencySkipped gateBlocker={blocker} blockerReason={blockerReason} registration=skipped");
                    continue;
                }

                if (!IsGateOpen(feature, ctx))
                {
                    lock (s_Lock) m_ClosedFeatures.Add(feature.Name);
                    Log.Info($"  closed: {feature.Name}");
                    Log.Info($"[FEATURE-VERIFY] feature={feature.Name} state=closed reason=GateClosed wave={wave} currentWave={manifest.CurrentWave} registration=skipped");
                    continue;
                }

                lock (s_Lock) m_OpenFeatures.Add(feature.Name);

                try
                {
                    Log.Info($"  open: {feature.Name} (priority {feature.Priority})");
                    Log.Info($"[FEATURE-VERIFY] feature={feature.Name} state=open wave={wave} currentWave={manifest.CurrentWave} registration=started");
                    using (ServiceRegistry.BeginFeatureRegistration(feature.Name))
                    {
                        if (feature is IContentFeatureModule content) content.RegisterContent();
                        feature.RegisterSystems(updateSystem);
                        if (feature is IUiFeatureModule ui) ui.RegisterUI(updateSystem);
                    }
                    Log.Info($"[FEATURE-VERIFY] feature={feature.Name} state=open registration=completed");
                }
                catch (Exception ex)
                {
                    failures++;
                    lock (s_Lock)
                    {
                        if (!m_FailedFeatures.Contains(feature.Name))
                            m_FailedFeatures.Add(feature.Name);
                        m_OpenFeatures.Remove(feature.Name);
                    }
                    Log.Error($"Failed to register feature {feature.Name}: {ex}");
                }
            }

            if (failures > 0)
            {
                List<string> snapshot;
                lock (s_Lock) snapshot = m_FailedFeatures.ToList();
                Log.Error($"CRITICAL: {failures} features failed registration: {string.Join(", ", snapshot)}");
            }

            lock (s_Lock) m_RegistrationComplete = true;

            Log.Info($"Features registered: {m_OpenFeatures.Count} open, {m_PreviewFeatures.Count} preview, {m_ClosedFeatures.Count} closed, {m_DepSkippedFeatures.Count} dep-skipped, {failures} failed");
            Log.Info($"[FEATURE-VERIFY] summary open={m_OpenFeatures.Count} preview={m_PreviewFeatures.Count} closed={m_ClosedFeatures.Count} depSkipped={m_DepSkippedFeatures.Count} failed={failures} currentWave={manifest.CurrentWave} sentinel={FeatureWaveConstants.WAVE_SENTINEL_UNAVAILABLE}");
            Log.Info("════════════════════════════════════════");
        }

        /// <summary>
        /// True if the named feature was registered AND its gate evaluated open
        /// AND no required dependency closed.
        /// </summary>
        public bool IsOpen(string featureId)
        {
            if (string.IsNullOrEmpty(featureId)) return false;
            lock (s_Lock) return m_OpenFeatures.Contains(featureId);
        }

        public bool IsAvailable(string featureId)
        {
            if (string.IsNullOrEmpty(featureId)) return false;
            lock (s_Lock)
            {
                if (!m_RegistrationComplete) return false;
                return m_OpenFeatures.Contains(featureId) && !m_FailedFeatures.Contains(featureId);
            }
        }

        /// <summary>
        /// True once <see cref="RegisterAllFeatures"/> has finished. Before this
        /// returns true the open/closed/failed sets are still being populated and
        /// it is not safe to decide whether a missing service indicates a closed
        /// feature or a registration race. Used by feature-aware service lookups
        /// to surface boot-time wiring bugs.
        /// </summary>
        public bool IsRegistrationComplete
        {
            get { lock (s_Lock) return m_RegistrationComplete; }
        }

        private bool IsAvailableDuringRegistration(string featureId)
        {
            if (string.IsNullOrEmpty(featureId)) return false;
            lock (s_Lock)
            {
                return m_OpenFeatures.Contains(featureId) && !m_FailedFeatures.Contains(featureId);
            }
        }

        public bool IsUnavailable(string featureId, out FeatureUnavailableReason reason)
        {
            reason = FeatureUnavailableReason.None;
            if (string.IsNullOrEmpty(featureId)) return false;

            lock (s_Lock)
            {
                if (!m_RegistrationComplete)
                    return false;

                if (m_ClosedFeatures.Contains(featureId))
                {
                    reason = FeatureUnavailableReason.Closed;
                    return true;
                }

                if (m_PreviewFeatures.Contains(featureId))
                {
                    reason = FeatureUnavailableReason.WaveLocked;
                    return true;
                }

                if (m_DepSkippedFeatures.Contains(featureId))
                {
                    reason = FeatureUnavailableReason.DependencySkipped;
                    return true;
                }

                if (m_FailedFeatures.Contains(featureId) || !m_OpenFeatures.Contains(featureId))
                {
                    reason = FeatureUnavailableReason.Failed;
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Returns the system instance from the active world if it exists,
        /// null otherwise. Single legal way to reach across feature boundaries —
        /// World.GetOrCreateSystemManaged is forbidden outside CoreKernel.
        /// Closed feature -> system never registered -> Query returns null.
        /// </summary>
        public T? Query<T>() where T : ComponentSystemBase
        {
            World world;
            lock (s_Lock) world = m_World;
            return world?.GetExistingSystemManaged<T>();
        }

        /// <summary>
        /// Like <see cref="Query{T}"/> but throws if the system is missing.
        /// Use for hard dependencies — same-domain systems, or cross-domain
        /// systems behind an explicit IDependentFeatureModule.Dependencies entry.
        /// A null return from those means the registration order is wrong or
        /// the dependency declaration is missing — both are programmer errors.
        /// </summary>
        public T Require<T>() where T : ComponentSystemBase
            => Query<T>() ?? throw new InvalidOperationException(
                $"{typeof(T).Name} not registered — feature gate closed, dependency missing, or registration order wrong");

        public void Dispose()
        {
            List<IFeatureModule> featuresToDispose;
            var logLines = new List<string>();
            lock (s_Lock)
            {
                if (m_Disposed) return;
                m_Disposed = true;

                logLines.Add($"Disposing {m_Features.Count} features:");
                foreach (var feature in m_Features.Values)
                {
                    var disposable = feature is IDisposable ? " (IDisposable)" : "";
                    logLines.Add($"  - {feature.Name}{disposable}");
                }

                featuresToDispose = new List<IFeatureModule>(m_Features.Values);
                m_Features.Clear();
                m_OpenFeatures.Clear();
                m_ClosedFeatures.Clear();
                m_PreviewFeatures.Clear();
                m_DepSkippedFeatures.Clear();
                m_FailedFeatures.Clear();
                m_RegistrationComplete = false;
                m_SortedFeatures = null!;
                m_World = null!;
                m_Manifest = null!;

#pragma warning disable S2696 // Singleton lifecycle: instance disposes its own static slot under s_Lock.
                s_Instance = null!;
#pragma warning restore S2696
            }
            foreach (var line in logLines) Log.Info(line);

            foreach (var feature in featuresToDispose)
            {
                if (feature is IDisposable disposable)
                {
                    try { disposable.Dispose(); }
                    catch (Exception ex) { Log.Warn($"Dispose error for {feature.Name}: {ex}"); }
                }
            }

            Log.Info("Disposed");
        }

        // ════════════════════════════════════════════════════════════════════
        // PRIVATE
        // ════════════════════════════════════════════════════════════════════

        private IReadOnlyList<IFeatureModule> GetSortedFeatures()
        {
            lock (s_Lock)
            {
                if (m_Disposed) return Array.Empty<IFeatureModule>();
                if (m_SortedFeatures == null)
                {
                    m_SortedFeatures = m_Features.Values.OrderBy(f => f.Priority).ToList();
                }
                return m_SortedFeatures;
            }
        }

        private void WarnUnknownManifestEntries(IReadOnlyList<IFeatureModule> features, FeatureManifest manifest)
        {
            var ids = new HashSet<string>(features.Select(f => f.Name), StringComparer.Ordinal);
            foreach (var key in manifest.Waves.Keys)
            {
                if (!ids.Contains(key))
                    Log.Warn($"manifest wave entry '{key}' does not match any registered feature id — typo?");
            }
        }

        private void ValidateOwnedByFeatureIds()
        {
            Type[] types;
            try
            {
                types = typeof(FeatureRegistry).Assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Partial-load fallback: assembly load may surface a few unresolvable
                // types (test-only assemblies, conditional-comp types). Use whatever
                // the reflection layer managed to load. Log so the absence isn't silent
                // if a real load failure ever happens here.
                Log.Warn($"ValidateOwnedByFeatureIds: ReflectionTypeLoadException — using {ex.Types.Count(t => t != null)} of {ex.Types.Length} loaded types. First loader error: {ex.LoaderExceptions?.FirstOrDefault()?.Message ?? "(none)"}");
                types = ex.Types.Where(t => t != null).ToArray()!;
            }

            foreach (var type in types)
            {
                if (type == null || !type.IsInterface)
                    continue;
                if ((type.Namespace ?? string.Empty).StartsWith("CivicSurvival.Tests", StringComparison.Ordinal))
                    continue;

                var attr = type.GetCustomAttribute<OwnedByFeatureIdAttribute>(inherit: false);
                if (attr == null)
                    continue;

                if (!IsKnownFeatureId(attr.FeatureId))
                {
                    throw new InvalidOperationException(
                        $"{type.FullName} declares [OwnedByFeatureId(\"{attr.FeatureId}\")] but no feature module with that id is registered.");
                }
            }
        }

        private bool IsGateOpen(IFeatureModule feature, IFeatureGateContext ctx)
        {
            if (feature is IGatedFeatureModule gated && gated.Gate != null)
                return gated.Gate.IsOpen(ctx);
            return true; // No gate => AlwaysOpen
        }

        private bool HasUnavailableDependency(IFeatureModule feature, out string blocker, out FeatureUnavailableReason reason)
        {
            blocker = null!;
            reason = FeatureUnavailableReason.None;
            if (feature is not IDependentFeatureModule dep || dep.Dependencies == null)
                return false;

            foreach (var depId in dep.Dependencies)
            {
                lock (s_Lock)
                {
                    if (m_OpenFeatures.Contains(depId) && !m_FailedFeatures.Contains(depId))
                        continue;

                    blocker = depId;
                    if (m_ClosedFeatures.Contains(depId))
                        reason = FeatureUnavailableReason.Closed;
                    else if (m_PreviewFeatures.Contains(depId))
                        reason = FeatureUnavailableReason.WaveLocked;
                    else if (m_DepSkippedFeatures.Contains(depId))
                        reason = FeatureUnavailableReason.DependencySkipped;
                    else
                        reason = FeatureUnavailableReason.Failed;
                }

                return true;
            }
            return false;
        }

        private bool HasUnavailableGateDependency(IFeatureModule feature, out string blocker, out FeatureUnavailableReason reason)
        {
            blocker = null!;
            reason = FeatureUnavailableReason.None;
            if (feature is not IGatedFeatureModule gated || gated.Gate == null)
                return false;

            return TryFindUnavailableGateDependency(gated.Gate, out blocker, out reason);
        }

        private bool TryFindUnavailableGateDependency(FeatureGate gate, out string blocker, out FeatureUnavailableReason reason)
        {
            blocker = null!;
            reason = FeatureUnavailableReason.None;

            switch (gate)
            {
                case FeatureGate.RequiresFeature requires:
                    if (IsAvailableDuringRegistration(requires.FeatureId))
                        return false;
                    blocker = requires.FeatureId;
                    reason = ReasonDuringRegistration(requires.FeatureId);
                    return true;

                case FeatureGate.AllOf allOf when allOf.Gates != null:
                    foreach (var nested in allOf.Gates)
                    {
                        if (TryFindUnavailableGateDependency(nested, out blocker, out reason))
                            return true;
                    }
                    return false;

                default:
                    return false;
            }
        }

        private FeatureUnavailableReason ReasonDuringRegistration(string featureId)
        {
            lock (s_Lock)
            {
                if (m_ClosedFeatures.Contains(featureId))
                    return FeatureUnavailableReason.Closed;
                if (m_PreviewFeatures.Contains(featureId))
                    return FeatureUnavailableReason.WaveLocked;
                if (m_DepSkippedFeatures.Contains(featureId))
                    return FeatureUnavailableReason.DependencySkipped;
                if (m_FailedFeatures.Contains(featureId))
                    return FeatureUnavailableReason.Failed;
            }

            return FeatureUnavailableReason.Failed;
        }

        private sealed class GateContext : IFeatureGateContext
        {
            private readonly FeatureRegistry m_Registry;

            public GateContext(FeatureRegistry registry)
            {
                m_Registry = registry;
            }

            public bool IsFeatureOpen(string featureId) => m_Registry.IsOpen(featureId);
            public bool IsFeatureAvailable(string featureId) => m_Registry.IsAvailableDuringRegistration(featureId);
        }
    }
}
