using IP_Phone.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace IP_Phone.Services
{
    /// <summary>
    /// Manages call history log as a JSON-persisted list of CallHistoryEntry records.
    /// Supports add, update by ID, get recent/all, and clear operations. The file
    /// is loaded on construction and saved to disk after every mutation.
    /// </summary>
    public class CallHistoryService
    {
        private readonly string _filePath;
        private List<CallHistoryEntry> _entries;

        /// <summary>
        /// Initialises the service and loads existing history from the
        /// callhistory.json file in the application base directory.
        /// </summary>
        public CallHistoryService()
        {
            _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "callhistory.json");
            _entries = Load();
        }

        /// <summary>Adds a new entry to the history list and persists to disk.</summary>
        public void Add(CallHistoryEntry entry)
        {
            _entries.Add(entry);
            Save();
        }

        /// <summary>
        /// Finds an entry by ID and applies the provided action to it, then persists.
        /// Does nothing if no entry with the given ID exists.
        /// </summary>
        public void Update(string id, Action<CallHistoryEntry> update)
        {
            var entry = _entries.FirstOrDefault(e => e.Id == id);
            if (entry != null)
            {
                update(entry);
                Save();
            }
        }

        /// <summary>Returns all entries sorted newest-first (by StartTime descending).</summary>
        public List<CallHistoryEntry> GetAll() => _entries.OrderByDescending(e => e.StartTime).ToList();

        /// <summary>Returns the N most recent entries sorted newest-first. Defaults to 20.</summary>
        public List<CallHistoryEntry> GetRecent(int count = 20) =>
            _entries.OrderByDescending(e => e.StartTime).Take(count).ToList();

        /// <summary>Clears all history entries and persists the empty list to disk.</summary>
        public void Clear()
        {
            _entries.Clear();
            Save();
        }

        /// <summary>Serialises the history list to JSON on disk.</summary>
        private void Save()
        {
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }

        /// <summary>Deserialises the history from disk, returning an empty list if missing or corrupt.</summary>
        private static List<CallHistoryEntry> Load()
        {
            try
            {
                var path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "callhistory.json");
                if (!File.Exists(path)) return new List<CallHistoryEntry>();
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<List<CallHistoryEntry>>(json) ?? new List<CallHistoryEntry>();
            }
            catch
            {
                return new List<CallHistoryEntry>();
            }
        }
    }
}
