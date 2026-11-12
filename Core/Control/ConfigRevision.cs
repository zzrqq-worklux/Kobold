using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace Kobold.Core.Control
{
    /// <summary>
    /// Content revision of the in-memory config. The canonical serialization is
    /// stable (widget kinds are emitted by <see cref="WidgetDataConverter"/>),
    /// so an unchanged config always hashes to the same value.
    /// </summary>
    public static class ConfigRevision
    {
        public static string Compute(AppConfig config)
        {
            string json = JsonConvert.SerializeObject(config, Formatting.None);

            using (var sha256 = SHA256.Create())
            {
                byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(json));
                var hex = new StringBuilder(hash.Length * 2);
                foreach (byte value in hash)
                {
                    hex.Append(value.ToString("x2", CultureInfo.InvariantCulture));
                }
                return "sha256:" + hex;
            }
        }
    }
}
