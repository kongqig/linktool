using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace linktool.Services
{
    /// <summary>
    /// 应用本地设置：持久化各页面的路径历史下拉选项。
    /// 存储位置：%LOCALAPPDATA%\LinkTool\settings.json
    /// </summary>
    public class AppSettings
    {
        private static readonly Lazy<AppSettings> _instance = new(() => new AppSettings());
        /// <summary>全局单例</summary>
        public static AppSettings Instance => _instance.Value;

        private const int MaxItems = 10;
        private readonly Dictionary<string, List<string>> _history = new();
        private readonly string _filePath;

        private AppSettings()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LinkTool");
            Directory.CreateDirectory(dir);
            _filePath = Path.Combine(dir, "settings.json");
            Load();
        }

        /// <summary>获取指定键的历史路径列表（新→旧）</summary>
        public IReadOnlyList<string> GetHistory(string key)
            => _history.TryGetValue(key, out var list) ? list : Array.Empty<string>();

        /// <summary>记录一条路径历史（去重、最新置顶、截断）并保存</summary>
        public void Remember(string key, string path)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(path))
                return;
            path = path.Trim();
            if (!_history.TryGetValue(key, out var list))
            {
                list = new List<string>();
                _history[key] = list;
            }
            list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, path);
            if (list.Count > MaxItems)
                list.RemoveRange(MaxItems, list.Count - MaxItems);
            Save();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_filePath)) return;
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(json);
                if (data == null) return;
                foreach (var kv in data)
                {
                    if (!string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                        _history[kv.Key] = kv.Value.Take(MaxItems).ToList();
                }
            }
            catch { /* 设置损坏时忽略，使用默认空历史 */ }
        }

        private void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(_history);
                File.WriteAllText(_filePath, json);
            }
            catch { /* 保存失败不影响主流程 */ }
        }
    }
}