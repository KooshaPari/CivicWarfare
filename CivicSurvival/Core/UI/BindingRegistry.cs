using System;
using System.Collections.Generic;
using System.Diagnostics;
using Colossal.UI.Binding;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Core.UI
{
    /// <summary>
    /// Registry for managing ValueBindings with less boilerplate.
    /// Replaces individual field declarations with a dictionary-based approach.
    /// Implements IDisposable to properly clean up binding references.
    ///
    /// Before (339+ individual fields across 13 panels):
    /// <code>
    /// private ValueBinding&lt;int&gt; m_WaveNumber;
    /// private ValueBinding&lt;string&gt; m_WavePhase;
    /// // ... 20+ more fields
    ///
    /// m_WaveNumber = new ValueBinding&lt;int&gt;("CivicSurvival", "waveNumber", 0);
    /// registrar.AddBinding(m_WaveNumber);
    /// // ... 20+ more registrations
    /// </code>
    ///
    /// After:
    /// <code>
    /// private readonly BindingRegistry _bindings = new();
    ///
    /// _bindings.Add&lt;int&gt;("waveNumber", 0);
    /// _bindings.Add&lt;string&gt;("wavePhase", "calm");
    /// _bindings.RegisterAll(registrar);
    /// </code>
    /// </summary>
    public sealed class BindingRegistry : IDisposable
    {
        private static readonly LogContext Log = new("BindingRegistry");

        private readonly string _modName;
        private readonly Dictionary<string, IBinding> _bindings = new();
        private readonly Dictionary<string, object> _typedBindings = new();
        private readonly Dictionary<string, object> _lastValueCells = new();

        // Static profiling (shared across all instances, drained by PerformanceProfiler)
        private static readonly Dictionary<string, BindingProfileData> s_ProfileData = new();
        private static readonly object s_ProfileLock = new();

        private sealed class BindingProfileData
        {
            public int UpdateCount;
            public int SkipCount;
            public long TotalChars;
            public long TotalTicks;
            // Time spent building the payload (DTO -> JSON string) before the
            // ValueBinding.Update push. TotalTicks above is push-only.
            public long TotalBuildTicks;
        }

        private sealed class LastValueCell<T>
        {
            public bool HasValue;
            public T Value = default!;
        }

        public BindingRegistry(string modName = "CivicSurvival")
        {
            _modName = modName;
        }

        /// <summary>
        /// Add a new ValueBinding with the specified key and default value.
        /// </summary>
        public ValueBinding<T> Add<T>(string key, T defaultValue)
        {
            if (_bindings.ContainsKey(key))
                throw new InvalidOperationException($"Binding '{key}' already registered in BindingRegistry");
            var binding = new ValueBinding<T>(_modName, key, defaultValue);
            _bindings[key] = binding;
            _typedBindings[key] = binding;
            _lastValueCells[key] = new LastValueCell<T>();
            return binding;
        }

        /// <summary>
        /// Add a new ValueBinding with a custom writer.
        /// </summary>
        public ValueBinding<T> Add<T>(string key, T defaultValue, IWriter<T> writer)
        {
            if (_bindings.ContainsKey(key))
                throw new InvalidOperationException($"Binding '{key}' already registered in BindingRegistry");
            var binding = new ValueBinding<T>(_modName, key, defaultValue, writer);
            _bindings[key] = binding;
            _typedBindings[key] = binding;
            _lastValueCells[key] = new LastValueCell<T>();
            return binding;
        }

        /// <summary>
        /// Add a GetterValueBinding (auto-updates from getter function).
        /// Getter bindings must be registered through AddUpdateBinding; RegisterAll
        /// preserves that lifecycle automatically.
        /// </summary>
        public GetterValueBinding<T> AddGetter<T>(string key, Func<T> getter)
        {
            if (_bindings.ContainsKey(key))
                throw new InvalidOperationException($"Binding '{key}' already registered in BindingRegistry");
            var binding = new GetterValueBinding<T>(_modName, key, getter);
            _bindings[key] = binding;
            _typedBindings[key] = binding;
            return binding;
        }

        /// <summary>
        /// Get a typed binding by key. Returns null if not found.
        /// Consider using TryGet for safer access.
        /// </summary>
        public ValueBinding<T>? Get<T>(string key)
        {
            if (!_typedBindings.TryGetValue(key, out var binding))
                return null;

            if (binding is ValueBinding<T> typed)
                return typed;

            throw CreateBindingTypeMismatch<T>(key, binding);
        }

        /// <summary>
        /// Try to get a typed binding by key.
        /// </summary>
        /// <returns>True if binding found and type matches, false otherwise</returns>
        public bool TryGet<T>(string key, out ValueBinding<T>? binding)
        {
            if (!_typedBindings.TryGetValue(key, out var obj))
            {
                binding = null;
                return false;
            }

            if (obj is ValueBinding<T> typed)
            {
                binding = typed;
                return true;
            }

            throw CreateBindingTypeMismatch<T>(key, obj);
        }

        /// <summary>
        /// Check if binding exists with given key.
        /// </summary>
        public bool Contains(string key) => _bindings.ContainsKey(key);

        /// <summary>
        /// Update a binding by key.
        /// Only calls ValueBinding.Update() if value actually changed (performance optimization).
        /// Logs debug warning if key not found or type mismatch.
        /// </summary>
        public void Update<T>(string key, T value)
        {
            if (!_typedBindings.TryGetValue(key, out var binding))
            {
                if (Log.IsDebugEnabled) Log.Debug($"Update failed: key '{key}' not found");
                return;
            }

            if (binding is not ValueBinding<T> typed)
                throw CreateBindingTypeMismatch<T>(key, binding);

            // Check if value changed before updating (performance: avoid 16k+ updates/sec)
            if (_lastValueCells.TryGetValue(key, out var cellObj) && cellObj is LastValueCell<T> cell)
            {
                if (cell.HasValue && EqualityComparer<T>.Default.Equals(cell.Value, value))
                {
                    ProfileSkip(key);
                    return;  // Value unchanged, skip expensive Update() call
                }

                cell.Value = value;
                cell.HasValue = true;
            }

            if (PerformanceProfiler.Enabled)
            {
                long start = Stopwatch.GetTimestamp();
                typed.Update(value);
                long elapsed = Stopwatch.GetTimestamp() - start;
#pragma warning disable CIVIC005 // Null guard is intentional — value can be null for reference types
                int chars = value is string s ? s.Length : value?.ToString()?.Length ?? 0;
#pragma warning restore CIVIC005
                ProfileUpdate(key, elapsed, chars);
            }
            else
            {
                typed.Update(value);
            }
        }

        /// <summary>
        /// Update a binding from a service with null-safety.
        /// Uses cached change detection for performance.
        /// </summary>
        /// <param name="key">Binding key</param>
        /// <param name="service">Service instance (can be null)</param>
        /// <param name="getter">Function to extract value from service</param>
        /// <param name="defaultValue">Value to use when service is null or getter throws</param>
        public void UpdateFrom<T, TService>(string key, TService? service, Func<TService, T> getter, T defaultValue)
            where TService : class
        {
            T value = defaultValue;

            if (service != null)
            {
                try
                {
                    value = getter(service);
                }
                catch (Exception ex)
                {
                    if (Log.IsDebugEnabled) Log.Debug($"Getter for '{key}' threw: {ex.Message}");
                }
            }

            // Reuse Update<T> which has change detection
            Update(key, value);
        }

        /// <summary>
        /// Register all bindings with the registrar.
        /// </summary>
        public void RegisterAll(IBindingRegistrar registrar)
        {
            foreach (var binding in _bindings.Values)
            {
                if (binding is IUpdateBinding updateBinding)
                    registrar.AddUpdateBinding(updateBinding);
                else
                    registrar.AddBinding(binding);
            }
        }

        /// <summary>
        /// Get all bindings for iteration.
        /// </summary>
        public IEnumerable<IBinding> All => _bindings.Values;

        /// <summary>
        /// Get binding count.
        /// </summary>
        public int Count => _bindings.Count;

        /// <summary>
        /// Drain profiling snapshot and reset counters. Called by PerformanceProfiler.Report().
        /// Returns empty list if no data was collected.
        /// </summary>
        public static List<(string Key, int Updates, int Skips, long Chars, long Ticks, long BuildTicks)> DrainProfileData()
        {
            lock (s_ProfileLock)
            {
                var result = new List<(string, int, int, long, long, long)>(s_ProfileData.Count);
                foreach (var kvp in s_ProfileData)
                {
                    result.Add((kvp.Key, kvp.Value.UpdateCount, kvp.Value.SkipCount,
                        kvp.Value.TotalChars, kvp.Value.TotalTicks, kvp.Value.TotalBuildTicks));
                }
                s_ProfileData.Clear();
                return result;
            }
        }

        private static void ProfileSkip(string key)
        {
            if (!PerformanceProfiler.Enabled) return;
            lock (s_ProfileLock)
            {
                if (!s_ProfileData.TryGetValue(key, out var data))
                {
                    data = new BindingProfileData();
                    s_ProfileData[key] = data;
                }
                data.SkipCount++;
            }
        }

        private static void ProfileUpdate(string key, long ticks, long chars)
        {
            if (!PerformanceProfiler.Enabled) return;
            lock (s_ProfileLock)
            {
                if (!s_ProfileData.TryGetValue(key, out var data))
                {
                    data = new BindingProfileData();
                    s_ProfileData[key] = data;
                }
                data.UpdateCount++;
                data.TotalTicks += ticks;
                data.TotalChars += chars;
            }
        }

        /// <summary>
        /// Record an external binding update for profiling (used by ProfiledBinding).
        /// </summary>
        public static void RecordExternalUpdate(string key, long ticks)
        {
            if (!PerformanceProfiler.Enabled) return;
            ProfileUpdate(key, ticks, 0);
        }

        /// <summary>
        /// Record payload build time (DTO -> JSON string) for a binding key.
        /// Separate from the push elapsed measured in <see cref="Update{T}"/>.
        /// </summary>
        public static void RecordBuildTime(string key, long ticks)
        {
            if (!PerformanceProfiler.Enabled) return;
            lock (s_ProfileLock)
            {
                if (!s_ProfileData.TryGetValue(key, out var data))
                {
                    data = new BindingProfileData();
                    s_ProfileData[key] = data;
                }
                data.TotalBuildTicks += ticks;
            }
        }

        private static InvalidOperationException CreateBindingTypeMismatch<T>(string key, object binding)
        {
            return new InvalidOperationException(
                $"Binding '{key}' is registered as {binding.GetType().Name}, not ValueBinding<{typeof(T).Name}>");
        }

        /// <summary>
        /// Clear all binding references and cached values.
        /// Call on dispose or when resetting state.
        /// </summary>
        /// <remarks>
        /// This method clears the registry's references to bindings but does NOT dispose
        /// the bindings themselves. Bindings are owned by CS2's UISystemBase which manages
        /// their lifecycle. The registry only holds references for convenience access.
        /// </remarks>
        public void Dispose()
        {
            _bindings.Clear();
            _typedBindings.Clear();
            _lastValueCells.Clear();
        }
    }
}
