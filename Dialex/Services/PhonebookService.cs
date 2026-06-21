using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IP_Phone.Services
{
    /// <summary>
    /// Manages a name-to-number phonebook persisted as phonebook.json.
    /// Supports lookup by name or number, add, remove, and clear operations.
    /// All entries are held in a case-insensitive dictionary. The file is
    /// loaded on construction and saved to disk after every mutation.
    /// </summary>
    public class PhonebookService
    {
        private readonly string _filePath;
        private Dictionary<string, string> _entries;

        /// <summary>
        /// Initialises the service and loads existing entries from the
        /// phonebook.json file in the application base directory.
        /// </summary>
        public PhonebookService()
        {
            _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "phonebook.json");
            _entries = Load();
        }

        /// <summary>Returns a copy of all entries with case-insensitive key comparison.</summary>
        public Dictionary<string, string> GetAll() => new Dictionary<string, string>(_entries, StringComparer.OrdinalIgnoreCase);

        /// <summary>Looks up a phone number by contact name. Returns null if not found.</summary>
        public string GetNumber(string name)
        {
            _entries.TryGetValue(name, out var number);
            return number;
        }

        /// <summary>Looks up a contact name by phone number. Returns null if not found.</summary>
        public string GetName(string number)
        {
            return _entries.FirstOrDefault(kv => kv.Value == number).Key;
        }

        /// <summary>Adds or updates a contact and persists to disk.</summary>
        public void Add(string name, string number)
        {
            _entries[name] = number;
            Save();
        }

        /// <summary>Removes a contact by name and persists to disk. Returns true if the entry existed.</summary>
        public bool Remove(string name)
        {
            var result = _entries.Remove(name);
            if (result) Save();
            return result;
        }

        /// <summary>Clears all contacts and persists to disk.</summary>
        public void Clear()
        {
            _entries.Clear();
            Save();
        }

        /// <summary>Serialises the phonebook to JSON on disk.</summary>
        private void Save()
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }

        /// <summary>Deserialises the phonebook from disk, returning empty if missing or corrupt.</summary>
        private static Dictionary<string, string> Load()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "phonebook.json");
                if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json)
                    ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}
