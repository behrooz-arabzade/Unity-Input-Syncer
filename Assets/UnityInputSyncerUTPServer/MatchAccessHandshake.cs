using System;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using Unity.Collections;
using UnityInputSyncerCore;

namespace UnityInputSyncerUTPServer
{
    public static class MatchAccessHandshake
    {
        public const int MaxPayloadBytes = 2048;

        public static bool Validate(InputSyncerServerOptions options, NativeArray<byte> data)
        {
            if (data.Length > MaxPayloadBytes)
                return false;

            byte[] copy = new byte[data.Length];
            data.CopyTo(copy);
            string json = Encoding.UTF8.GetString(copy);

            JObject obj;
            try
            {
                obj = string.IsNullOrWhiteSpace(json)
                    ? new JObject()
                    : JObject.Parse(json);
            }
            catch
            {
                return false;
            }

            switch (options.MatchAccess)
            {
                case MatchAccessMode.Open:
                    return true;
                case MatchAccessMode.Password:
                {
                    string provided = obj["matchPassword"]?.Value<string>();
                    if (string.IsNullOrEmpty(provided) || string.IsNullOrEmpty(options.MatchPassword))
                        return false;
                    return PasswordEquals(options.MatchPassword, provided);
                }
                case MatchAccessMode.Token:
                {
                    string token = obj["matchToken"]?.Value<string>();
                    if (string.IsNullOrEmpty(token))
                        return false;
                    return options.AllowedMatchTokens != null && options.AllowedMatchTokens.Contains(token);
                }
                default:
                    return false;
            }
        }

        static bool PasswordEquals(string expected, string actual)
        {
            byte[] e;
            byte[] a;
            using (var sha = SHA256.Create())
            {
                e = sha.ComputeHash(Encoding.UTF8.GetBytes(expected));
            }
            using (var sha = SHA256.Create())
            {
                a = sha.ComputeHash(Encoding.UTF8.GetBytes(actual));
            }
            return CryptographicOperations.FixedTimeEquals(e, a);
        }
    }
}
