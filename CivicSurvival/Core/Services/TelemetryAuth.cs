using System;
using System.IO;
using System.Text;
using CivicSurvival.Core.Attributes;
using CivicSurvival.Core.Config;
using CivicSurvival.Core.Types;
using CivicSurvival.Core.Utils;

namespace CivicSurvival.Core.Services
{
    /// <summary>
    /// Player authentication and credentials management.
    /// Handles persistent player ID, server registration, and nickname registration.
    /// </summary>
    [OutboundTelemetry]
    public sealed class TelemetryAuth
    {
        private const int MAX_CREDENTIAL_FILE_SIZE = 1_048_576;
        private const int MAX_AUTH_TOKEN_LENGTH = 4096;
        private const int STATUS_UNAUTHORIZED = 401;
        private const int STATUS_FORBIDDEN = 403;
        private const int STATUS_CONFLICT = 409;
        private const int STATUS_TOO_MANY_REQUESTS = 429;
        private const string CREDENTIALS_FORMAT_V2 = "v2";

        private static readonly LogContext Log = new("TelemetryAuth");
        private readonly TelemetryConfig m_Config;
        private readonly ICredentialProtector m_CredentialProtector;
        private readonly object m_Lock = new();

        private string m_PlayerId = string.Empty;
        private string m_AuthToken = string.Empty;
        private bool m_IsRegistered;

        // TS-004 FIX: Thread-safe property accessors (fields written from ThreadPool)
        public string PlayerId { get { lock (m_Lock) return m_PlayerId; } }
        public string AuthToken { get { lock (m_Lock) return m_AuthToken; } }
        public bool IsRegistered { get { lock (m_Lock) return m_IsRegistered; } }

        public TelemetryAuth(TelemetryConfig config, ICredentialProtector? credentialProtector = null)
        {
            m_Config = config ?? throw new ArgumentNullException(nameof(config));
            m_CredentialProtector = credentialProtector ?? CredentialProtectorFactory.Create();
        }

        public void LoadOrCreateCredentials()
        {
            var path = GetCredentialsPath();
            var bakPath = path + ".bak";

            // Try primary file first, then backup
            if (TryLoadCredentials(path) || TryLoadCredentials(bakPath))
            {
                return;
            }

            // First run or both files corrupt — generate new player_id
            lock (m_Lock)
            {
                m_PlayerId = Guid.NewGuid().ToString();
                m_AuthToken = "";
                m_IsRegistered = false;
            }

            SaveCredentials();
#pragma warning disable CIVIC114 // Constructor context — no concurrent threads yet
            var prefix = m_PlayerId.Length >= 8 ? m_PlayerId.Substring(0, 8) : m_PlayerId;
#pragma warning restore CIVIC114
            Log.Info($" New player created: {prefix}...");
        }

        private bool TryLoadCredentials(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return false;
                var fi = new FileInfo(filePath);
                if (fi.Length > MAX_CREDENTIAL_FILE_SIZE) return false; // 1 MB guard — credentials is 2 lines

#pragma warning disable CIVIC028 // FileInfo.Length guard above
                var lines = File.ReadAllLines(filePath);
#pragma warning restore CIVIC028
                if (lines.Length < 2) return false;

                var isV2 = string.Equals(lines[0].Trim(), CREDENTIALS_FORMAT_V2, StringComparison.Ordinal);

                // A v2 record is always three lines (marker, playerId, protected token —
                // see SaveCredentials). A truncated "v2" file with only two lines is corrupt:
                // reject it cleanly here so the .bak fallback runs, instead of letting lines[2]
                // throw IndexOutOfRangeException (silently swallowed → identity loss).
                if (isV2 && lines.Length < 3) return false;

                var playerId = isV2 ? lines[1].Trim() : lines[0].Trim();
                var authToken = isV2 ? UnprotectToken(lines[2].Trim(), filePath) : lines[1].Trim();

                // Validate GUID format to detect corruption
                if (!Guid.TryParse(playerId, out _))
                {
                    Log.Warn($" Corrupt player_id in {Path.GetFileName(filePath)}: not a valid GUID");
                    return false;
                }

                if (!IsValidStoredAuthToken(authToken))
                {
                    Log.Warn($" Corrupt auth_token in {Path.GetFileName(filePath)}");
                    return false;
                }

                lock (m_Lock)
                {
                    m_PlayerId = playerId;
                    m_AuthToken = authToken;
                    m_IsRegistered = !string.IsNullOrEmpty(m_AuthToken);
                }

#pragma warning disable CIVIC114 // Constructor context — no concurrent threads yet
                var prefix = m_PlayerId.Length >= 8 ? m_PlayerId.Substring(0, 8) : m_PlayerId;
#pragma warning restore CIVIC114
                var source = filePath.EndsWith(".bak") ? " (from backup)" : "";
                if (Log.IsDebugEnabled) Log.Debug($" Loaded credentials{source}: player={prefix}..., registered={m_IsRegistered}");
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn($" Failed to load {Path.GetFileName(filePath)}: {ex.GetType().Name}");
                return false;
            }
        }

        public void RegisterPlayerAsync(Action? onSuccess = null)
        {
            if (!m_Config.OnlineEnabled)
            {
                Log.Debug(" Online disabled, skipping server registration");
                return;
            }

            // TS-005 FIX: Use thread-safe property accessor
            var url = m_Config.ServerUrl + "/auth/register";
            var json = JsonBuilder.Object().Add("player_id", PlayerId).Build();

BackgroundTask.Run(() =>
            {
                try
                {
                    var result = HttpUtils.Post(url, json);
                    if (!result.Success)
                    {
                        Log.Warn($" Player registration failed: {result.ErrorMessage}");
                        return;
                    }

                    // Parse auth_token from response (no AST build — direct top-level scan)
                    var authToken = JsonStream.TryReadStringField(result.Response, "auth_token") ?? "";
                    if (IsValidAuthToken(authToken))
                    {
                        lock (m_Lock)
                        {
                            m_AuthToken = authToken!;
                            m_IsRegistered = true;
                        }
                        SaveCredentials();

                        Log.Info(" Player registered with server");
                        onSuccess?.Invoke();
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($" Registration error: {ex.GetType().Name}");
                }
            });
        }

        public readonly struct NicknameRegistrationResult
        {
            /// <summary>Sentinel for an unknown change budget (server did not report one).</summary>
            public const int ChangesRemainingUnknown = -1;

            public readonly bool Success;
            public readonly string ReasonId;
            public readonly int ChangesRemaining;
            public readonly bool Initialized;

            /// <summary>
            /// The player's current nickname as held by the server (source of truth for the
            /// online nickname). Empty when the server reported no nickname. Only meaningful
            /// together with <see cref="Initialized"/> == true — a never-set player is reported
            /// with Initialized=false and an empty Nickname. The client NEVER fabricates an
            /// identity: any synthetic display fallback the server might emit is defensively
            /// dropped here (see <see cref="IsSyntheticFallback"/>). Callers must treat an
            /// Initialized=false result as "not signed in" / "nickname not yet chosen" and MUST
            /// NOT render the Nickname field as if it were a real chosen identity.
            /// </summary>
            public readonly string Nickname;

            public NicknameRegistrationResult(
                bool success,
                string reasonId,
                int changesRemaining = ChangesRemainingUnknown,
                bool initialized = false,
                string nickname = "")
            {
                Success = success;
                ReasonId = reasonId ?? string.Empty;
                ChangesRemaining = changesRemaining;
                // Defensive drop: if the server ever emits a synthetic display
                // fallback (e.g. "Mayor_XXXX" from an older build) we never
                // surface it as a real identity. The client treats this as
                // "not signed in" and the UI MUST render "nickname not set".
                Initialized = initialized && !string.IsNullOrEmpty(nickname) && !IsSyntheticFallback(nickname);
                Nickname = (initialized && !IsSyntheticFallback(nickname)) ? nickname : string.Empty;
            }

            /// <summary>
            /// True for synthetic display strings the server has historically
            /// returned for never-set players. The current server contract
            /// reports Initialized=false instead, but defense in depth: any
            /// future server that slips back to a display fallback is caught
            /// here and the value is dropped before reaching UI or persistence.
            /// </summary>
            internal static bool IsSyntheticFallback(string? value)
            {
                if (string.IsNullOrEmpty(value)) return false;
                return value.StartsWith("Mayor_", StringComparison.Ordinal)
                    || value.StartsWith("Mayor ", StringComparison.Ordinal)
                    || value.Equals("Mayor", StringComparison.Ordinal);
            }
        }

        public void RegisterNicknameAsync(string nickname, Action<NicknameRegistrationResult>? onComplete = null)
        {
            if (!m_Config.OnlineEnabled)
            {
                Log.Debug(" Online disabled, nickname not sent to server");
                onComplete?.Invoke(new NicknameRegistrationResult(false, ReasonIds.NicknameServerUnavailable));
                return;
            }

            // TS-005 FIX: Use thread-safe property accessors
            if (!IsRegistered || string.IsNullOrEmpty(AuthToken))
            {
                Log.Debug(" Not registered yet, cannot register nickname");
                onComplete?.Invoke(new NicknameRegistrationResult(false, ReasonIds.NicknameServerUnavailable));
                return;
            }

            // TS-005 FIX: Capture thread-safe copies before ThreadPool
            var playerId = PlayerId;
            var authToken = AuthToken;
            var url = m_Config.ServerUrl + "/nickname";
            var json = JsonBuilder.Object()
                .Add("player_id", playerId)
                .Add("nickname", nickname)
                .Build();

BackgroundTask.Run(() =>
            {
                var result = HttpUtils.Post(url, json, authToken);
                if (result.Success)
                {
                    int remaining = JsonStream.TryReadIntField(
                        result.Response, "changes_remaining", NicknameRegistrationResult.ChangesRemainingUnknown);
                    bool initialized = JsonStream.TryReadBoolField(result.Response, "initialized", true);
                    string echo = JsonStream.TryReadStringField(result.Response, "nickname") ?? nickname;
                    Log.Info($" Nickname registered: {nickname} (changes remaining: {remaining})");
                    onComplete?.Invoke(new NicknameRegistrationResult(true, string.Empty, remaining, initialized, echo));
                }
                else
                {
                    Log.Warn($" Nickname registration failed: {result.ErrorMessage}");
                    if (IsUnauthorized(result.StatusCode))
                        InvalidateToken();
                    onComplete?.Invoke(new NicknameRegistrationResult(false, MapNicknameFailure(result)));
                }
            });
        }

        // Server nickname rejection codes (stable error_code values from the 409 body) → UI
        // reasons. Immutable lookup table (CIVIC135: dictionary over switch-on-string).
#pragma warning disable CIVIC148 // immutable hardcoded lookup table — no runtime accumulation
        private static readonly System.Collections.Generic.Dictionary<string, ReasonId> s_NicknameFailureReasons = new()
        {
            ["taken"] = ReasonIds.NicknameTaken,
            ["restricted"] = ReasonIds.NicknameRestricted,
            ["length"] = ReasonIds.NicknameInvalidLength,
            ["charset"] = ReasonIds.NicknameInvalidChars,
            ["no_changes"] = ReasonIds.NicknameNoChanges,
        };
#pragma warning restore CIVIC148

        // Map a failed nickname registration response to a specific UI reason. The server
        // returns a machine-readable "error_code" in the 409 body; a 429 is the rate limiter.
        // Anything else collapses to the generic server-unavailable reason.
        private static ReasonId MapNicknameFailure(HttpUtils.HttpResult result)
        {
            if (result.StatusCode == STATUS_TOO_MANY_REQUESTS)
                return ReasonIds.NicknameRateLimited;

            if (result.StatusCode == STATUS_CONFLICT)
            {
                var code = JsonStream.TryReadStringField(result.Response, "error_code") ?? string.Empty;
                return s_NicknameFailureReasons.TryGetValue(code, out var reason)
                    ? reason
                    : ReasonIds.NicknameServerUnavailable;
            }

            return ReasonIds.NicknameServerUnavailable;
        }

        /// <summary>
        /// Read-only fetch of the player's nickname change-budget status from the server.
        /// Does not consume the monthly budget. Fire-and-forget; result delivered on a
        /// ThreadPool thread, so the callback must marshal to the main thread itself.
        /// </summary>
        public void FetchNicknameStatusAsync(Action<NicknameRegistrationResult>? onComplete = null)
        {
            if (!m_Config.OnlineEnabled)
            {
                onComplete?.Invoke(new NicknameRegistrationResult(false, ReasonIds.NicknameServerUnavailable));
                return;
            }

            var playerId = PlayerId;
            if (string.IsNullOrEmpty(playerId))
            {
                onComplete?.Invoke(new NicknameRegistrationResult(false, ReasonIds.NicknameServerUnavailable));
                return;
            }

            var url = m_Config.ServerUrl + "/nickname/" + playerId;
            BackgroundTask.Run(() =>
            {
                var result = HttpUtils.Get(url);
                if (!result.Success)
                {
                    onComplete?.Invoke(new NicknameRegistrationResult(false, ReasonIds.NicknameServerUnavailable));
                    return;
                }

                int remaining = JsonStream.TryReadIntField(
                    result.Response, "changes_remaining", NicknameRegistrationResult.ChangesRemainingUnknown);
                bool initialized = JsonStream.TryReadBoolField(result.Response, "initialized", false);
                string nickname = JsonStream.TryReadStringField(result.Response, "nickname") ?? string.Empty;
                onComplete?.Invoke(new NicknameRegistrationResult(true, string.Empty, remaining, initialized, nickname));
            });
        }

        private string GetCredentialsPath()
        {
            var root = Path.GetFullPath(m_Config.ModDataDirectory);
            var path = Path.GetFullPath(Path.Combine(root, ModPaths.CredentialsFile));
            var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? root
                : root + Path.DirectorySeparatorChar;

            if (!path.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Credentials path escaped mod data directory");

            if (!Directory.Exists(root))
            {
                Directory.CreateDirectory(root);
            }

            return path;
        }

        private void SaveCredentials()
        {
            try
            {
                var path = GetCredentialsPath();

                // Backup existing protected credentials before overwrite. Never preserve plaintext tokens.
#pragma warning disable CIVIC064 // File.Exists needed: Copy throws on missing source
                if (File.Exists(path) && IsProtectedCredentialsFile(path))
                {
#pragma warning restore CIVIC064
#pragma warning disable CIVIC052 // Best-effort backup before atomic write
                    try { File.Copy(path, path + ".bak", overwrite: true); }
                    catch { /* best-effort backup */ }
#pragma warning restore CIVIC052
                }

                // FIX H88: Read token under lock — SaveCredentials called after lock release
                string playerId;
                string token;
                lock (m_Lock)
                {
                    playerId = m_PlayerId ?? "";
                    token = m_AuthToken ?? "";
                }
                AtomicFileWriter.WriteAllLines(path, new[] { CREDENTIALS_FORMAT_V2, playerId, ProtectToken(token) });
                Log.Debug(" Credentials saved");
            }
            catch (Exception ex)
            {
                Log.Error($" Failed to save credentials: {ex.GetType().Name}");
            }
        }

        public void InvalidateToken()
        {
            lock (m_Lock)
            {
                m_AuthToken = "";
                m_IsRegistered = false;
            }

            SaveCredentials();

            RegisterPlayerAsync();
        }

        private static bool IsUnauthorized(int statusCode) => statusCode == STATUS_UNAUTHORIZED || statusCode == STATUS_FORBIDDEN;

        private static bool IsProtectedCredentialsFile(string path)
        {
            try
            {
                using var reader = new StreamReader(path, Encoding.UTF8, true, 64);
                return string.Equals(reader.ReadLine()?.Trim(), CREDENTIALS_FORMAT_V2, StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                if (Log.IsDebugEnabled) Log.Debug($" Protected credentials probe failed: {ex.GetType().Name}");
                return false;
            }
        }

        private static bool IsValidAuthToken(string? token)
        {
            return !string.IsNullOrEmpty(token) && IsValidStoredAuthToken(token);
        }

        private static bool IsValidStoredAuthToken(string? token)
        {
            if (token == null || token.Length > MAX_AUTH_TOKEN_LENGTH)
                return false;

            foreach (char c in token)
            {
                if (char.IsControl(c) || char.IsWhiteSpace(c))
                    return false;
            }

            return true;
        }

        private string ProtectToken(string token)
        {
            try
            {
                return m_CredentialProtector.Protect(token);
            }
            catch (Exception ex)
            {
                Log.Warn($" Token protection failed: {ex.GetType().Name}");
                return string.Empty;
            }
        }

        private string UnprotectToken(string protectedToken, string filePath)
        {
            try
            {
                return m_CredentialProtector.Unprotect(protectedToken);
            }
            catch (Exception ex)
            {
                Log.Warn($" Auth token unreadable in {Path.GetFileName(filePath)}: {ex.GetType().Name}; keeping player_id as unregistered");
                return string.Empty;
            }
        }
    }
}
