using System.Collections.Generic;
using System.Linq;

namespace linktool.Services
{
    /// <summary>粘贴解析出的单行：Col1 主路径必填，Col2 次路径可空</summary>
    public sealed record ParsedLine(string Col1, string? Col2);

    /// <summary>
    /// 批量粘贴文本解析。分隔符均为 Windows 路径禁用字符，不会与路径内容冲突：
    ///  - 换行 → 分行
    ///  - 无引号行按 “|” 分列：第1列=主路径，第2列=次路径，多余列忽略
    ///    （批量迁移：From|行To；批量链接：链接目录|目标；单列时按各页默认填充）
    ///  - 含引号行：提取每个 "..." 引号段，各作为一条主路径（不参与分列，
    ///    兼容资源管理器复制多文件夹产生的 "C:\a" "C:\b" 样式）
    /// </summary>
    public static class PastePathParser
    {
        public static List<ParsedLine> Parse(string text)
        {
            var result = new List<ParsedLine>();
            if (string.IsNullOrWhiteSpace(text)) return result;

            foreach (var rawLine in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line.Contains('"'))
                {
                    foreach (var seg in ExtractQuoted(line))
                        result.Add(new ParsedLine(seg, null));
                    continue;
                }

                var cols = line.Split('|').Select(c => c.Trim()).ToList();
                var col1 = cols.Count > 0 ? cols[0] : "";
                string? col2 = cols.Count > 1 && cols[1].Length > 0 ? cols[1] : null;
                if (col1.Length > 0)
                    result.Add(new ParsedLine(col1, col2));
            }
            return result;
        }

        /// <summary>提取一行中的成对引号段</summary>
        private static IEnumerable<string> ExtractQuoted(string line)
        {
            var segments = new List<string>();
            var idx = 0;
            while (idx < line.Length)
            {
                var start = line.IndexOf('"', idx);
                if (start < 0) break;
                var end = line.IndexOf('"', start + 1);
                if (end < 0) break; // 未闭合，忽略剩余
                var seg = line.Substring(start + 1, end - start - 1).Trim();
                if (seg.Length > 0) segments.Add(seg);
                idx = end + 1;
            }
            return segments;
        }
    }
}
