namespace IndusBrawl.Laser.Server.Utils
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using IndusBrawl.Laser.Logic.Util;
    using IndusBrawl.Laser.Server.Settings;

    // Контент-апдейты (фингерпринт) с безопасным тестом:
    // Error 7 отдаётся ТОЛЬКО аккам из content_test.txt (тэги, по одному на строку).
    // Остальные заходят как раньше. Файл отсутствует — проверки нет вообще.
    public static class ContentUpdateTest
    {
        private const string TEST_PATH = "content_test.txt";
        private static readonly HashSet<long> _ids = new HashSet<long>();
        private static DateTime _loadedAt = DateTime.MinValue;

        public static string ContentUrl
        {
            get
            {
                string cfg = null;
                try { cfg = Configuration.Instance?.ContentUrl; } catch { }
                if (!string.IsNullOrWhiteSpace(cfg)) return cfg.TrimEnd('/') + "/";
                string host = "127.0.0.1";
                int port = 8085;
                try
                {
                    host = Configuration.Instance.UdpHost ?? host;
                    if (Configuration.Instance.WebPort > 0) port = Configuration.Instance.WebPort;
                }
                catch { }
                return $"http://{host}:{port}/content/";
            }
        }

        public static bool IsTestAccount(long accountId)
        {
            try
            {
                if (!File.Exists(TEST_PATH)) return false;
                // строка ALL в файле = проверка для всех (смена для всего сервера)
                foreach (string line in File.ReadAllLines(TEST_PATH))
                {
                    if (line.Trim().ToUpper() == "ALL") return true;
                }
                if (File.GetLastWriteTimeUtc(TEST_PATH) > _loadedAt)
                {
                    _ids.Clear();
                    foreach (string line in File.ReadAllLines(TEST_PATH))
                    {
                        string tag = line.Trim().ToUpper();
                        if (tag == "" || tag.StartsWith("#") && tag.Length == 1) continue;
                        try { _ids.Add(LogicLongCodeGenerator.ToId(tag)); }
                        catch { }
                    }
                    _loadedAt = DateTime.UtcNow;
                }
                return _ids.Contains(accountId);
            }
            catch
            {
                return false;
            }
        }

        public static List<string> GetTestTags()
        {
            var list = new List<string>();
            try
            {
                if (File.Exists(TEST_PATH)) list.AddRange(File.ReadAllLines(TEST_PATH));
            }
            catch { }
            return list;
        }

        public static void SetTestTags(string text)
        {
            File.WriteAllText(TEST_PATH, text ?? "");
            _loadedAt = DateTime.MinValue;
        }
    }
}
