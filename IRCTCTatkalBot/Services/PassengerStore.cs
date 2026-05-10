using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using IRCTCTatkalBot.Models;

namespace IRCTCTatkalBot.Services
{
    /// <summary>
    /// Persists passenger list to AppData (editable from UI without recompile).
    /// </summary>
    public sealed class PassengerStore
    {
        private static readonly string PathFile = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "IRCTCTatkalBot", "passengers.json");

        public List<Passenger> Load()
        {
            try
            {
                if (!File.Exists(PathFile))
                    return new List<Passenger>();

                string json = File.ReadAllText(PathFile);
                var list = JsonSerializer.Deserialize<List<Passenger>>(json);
                return list ?? new List<Passenger>();
            }
            catch
            {
                return new List<Passenger>();
            }
        }

        public void Save(IReadOnlyList<Passenger> passengers)
        {
            string dir = System.IO.Path.GetDirectoryName(PathFile)!;
            Directory.CreateDirectory(dir);
            var opts = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(PathFile, JsonSerializer.Serialize(passengers.ToList(), opts));
        }
    }
}
