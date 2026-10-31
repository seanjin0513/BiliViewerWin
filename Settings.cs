using System;
using System.IO;
using System.Text.Json;

namespace BiliViewer
{
    /// <summary>用户设置 + 窗口位置，存 %LOCALAPPDATA%\BiliViewer\settings.json</summary>
    internal sealed class Settings
    {
        public static readonly string AppDataDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BiliViewer");

        static string FilePath => Path.Combine(AppDataDir, "settings.json");

        static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions { WriteIndented = true };

        public double? Left { get; set; }
        public double? Top { get; set; }
        public double? Width { get; set; }
        public double? Height { get; set; }
        public bool Maximized { get; set; }

        /// <summary>播放器滚轮调音量的灵敏度倍率：0.6 慢 / 1.0 标准 / 1.5 快</summary>
        public double Sensitivity { get; set; } = 1.0;

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var text = File.ReadAllText(FilePath);
                    var s = JsonSerializer.Deserialize<Settings>(text, JsonOptions);
                    if (s != null)
                    {
                        if (s.Sensitivity <= 0) s.Sensitivity = 1.0;
                        return s;
                    }
                }
            }
            catch
            {
                // 配置损坏就回到默认值，不打扰用户
            }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(AppDataDir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
            }
            catch
            {
                // 忽略写入失败（例如目录只读）
            }
        }
    }
}
