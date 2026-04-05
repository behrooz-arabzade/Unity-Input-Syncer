using System;
using System.Collections.Generic;
using UnityInputSyncerCore;

namespace UnityInputSyncerUTPServer
{
    internal static class MatchAccessCreateValidation
    {
        internal const int MaxTokens = 64;
        internal const int MaxTokenLength = 256;

        internal static void Validate(AdminCreateInstanceRequest request, ICollection<string> errors)
        {
            if (request == null)
                return;

            string raw = request.MatchAccess?.Trim();
            if (string.IsNullOrEmpty(raw))
                raw = "open";

            switch (raw.ToLowerInvariant())
            {
                case "open":
                    if (!string.IsNullOrEmpty(request.MatchPassword))
                        errors.Add("matchPassword must not be set when matchAccess is open");
                    if (request.AllowedMatchTokens != null && request.AllowedMatchTokens.Count > 0)
                        errors.Add("allowedMatchTokens must not be set when matchAccess is open");
                    break;
                case "password":
                    if (string.IsNullOrEmpty(request.MatchPassword))
                        errors.Add("matchPassword is required when matchAccess is password");
                    if (request.AllowedMatchTokens != null && request.AllowedMatchTokens.Count > 0)
                        errors.Add("allowedMatchTokens must not be set when matchAccess is password");
                    break;
                case "token":
                    if (!string.IsNullOrEmpty(request.MatchPassword))
                        errors.Add("matchPassword must not be set when matchAccess is token");
                    if (request.AllowedMatchTokens == null || request.AllowedMatchTokens.Count == 0)
                    {
                        errors.Add("allowedMatchTokens is required when matchAccess is token");
                        break;
                    }
                    var seen = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var t in request.AllowedMatchTokens)
                    {
                        if (string.IsNullOrWhiteSpace(t))
                        {
                            errors.Add("allowedMatchTokens entries must be non-empty");
                            break;
                        }
                        if (t.Length > MaxTokenLength)
                        {
                            errors.Add($"each token must be at most {MaxTokenLength} characters");
                            break;
                        }
                        seen.Add(t);
                    }
                    if (seen.Count > MaxTokens)
                        errors.Add($"at most {MaxTokens} distinct tokens allowed");
                    break;
                default:
                    errors.Add("matchAccess must be open, password, or token");
                    break;
            }
        }

        internal static void MapToOptions(AdminCreateInstanceRequest request, InputSyncerServerOptions o)
        {
            if (request == null)
                return;

            string raw = request.MatchAccess?.Trim();
            if (string.IsNullOrEmpty(raw))
                raw = "open";

            switch (raw.ToLowerInvariant())
            {
                case "open":
                    o.MatchAccess = MatchAccessMode.Open;
                    o.MatchPassword = "";
                    o.AllowedMatchTokens = null;
                    break;
                case "password":
                    o.MatchAccess = MatchAccessMode.Password;
                    o.MatchPassword = request.MatchPassword ?? "";
                    o.AllowedMatchTokens = null;
                    break;
                case "token":
                    o.MatchAccess = MatchAccessMode.Token;
                    o.MatchPassword = "";
                    o.AllowedMatchTokens = new HashSet<string>(StringComparer.Ordinal);
                    if (request.AllowedMatchTokens != null)
                    {
                        foreach (var t in request.AllowedMatchTokens)
                        {
                            if (!string.IsNullOrWhiteSpace(t))
                                o.AllowedMatchTokens.Add(t);
                        }
                    }
                    break;
            }
        }

        internal static string AccessModeToApiString(MatchAccessMode mode)
        {
            switch (mode)
            {
                case MatchAccessMode.Password:
                    return "password";
                case MatchAccessMode.Token:
                    return "token";
                default:
                    return "open";
            }
        }
    }
}
