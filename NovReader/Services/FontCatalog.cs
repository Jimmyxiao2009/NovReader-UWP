using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Media;

namespace NovReader.Services
{
    /// <summary>字体选项：界面显示中文名，保存与使用英文字体族名。</summary>
    public sealed class FontChoice
    {
        public string Label { get; set; }
        /// <summary>字体族名；空串表示系统默认字体。</summary>
        public string Family { get; set; }
        public override string ToString() { return Label; }
    }

    /// <summary>
    /// 阅读字体目录。
    /// XAML 只认字体族的英文名，写“微软雅黑”“楷体”在 Windows 10 Mobile 上找不到字体，会静默回退到默认字体，
    /// 看起来就是“换字体没用”。手机上也只装了少数中文字体，所以只列出本机确实存在的字体。
    /// </summary>
    public static class FontCatalog
    {
        private static readonly FontChoice[] Candidates =
        {
            new FontChoice { Label = "系统默认", Family = "" },
            new FontChoice { Label = "微软雅黑", Family = "Microsoft YaHei" },
            new FontChoice { Label = "微软雅黑 UI", Family = "Microsoft YaHei UI" },
            new FontChoice { Label = "等线", Family = "DengXian" },
            new FontChoice { Label = "宋体", Family = "SimSun" },
            new FontChoice { Label = "黑体", Family = "SimHei" },
            new FontChoice { Label = "楷体", Family = "KaiTi" },
            new FontChoice { Label = "仿宋", Family = "FangSong" },
            new FontChoice { Label = "微软正黑", Family = "Microsoft JhengHei" },
            new FontChoice { Label = "Segoe UI", Family = "Segoe UI" },
            new FontChoice { Label = "Georgia", Family = "Georgia" }
        };

        /// <summary>旧版本保存的是中文名，读取时换成字体族名。</summary>
        private static readonly Dictionary<string, string> Legacy = new Dictionary<string, string>
        {
            { "微软雅黑", "Microsoft YaHei" }, { "等线", "DengXian" }, { "宋体", "SimSun" },
            { "黑体", "SimHei" }, { "楷体", "KaiTi" }, { "仿宋", "FangSong" }
        };

        private static List<FontChoice> _available;

        public static string Normalize(string saved)
        {
            if (String.IsNullOrEmpty(saved)) return "";
            string mapped;
            return Legacy.TryGetValue(saved.Trim(), out mapped) ? mapped : saved.Trim();
        }

        /// <summary>本机可用的字体（需在 UI 线程调用，结果会缓存）。</summary>
        public static IList<FontChoice> Available()
        {
            if (_available != null) return _available;
            var list = new List<FontChoice>();
            Size missing = Measure("NovReader_No_Such_Font_8f3a");
            foreach (var c in Candidates)
            {
                if (c.Family.Length == 0) { list.Add(c); continue; }
                Size size = Measure(c.Family);
                bool fallback = Math.Abs(size.Width - missing.Width) < 0.5 && Math.Abs(size.Height - missing.Height) < 0.5;
                if (!fallback) list.Add(c);
            }
            _available = list;
            return list;
        }

        /// <summary>按字体族名找到选项；找不到（字体不在本机）时返回“系统默认”。</summary>
        public static FontChoice Find(string family)
        {
            string f = Normalize(family);
            var list = Available();
            for (int i = 0; i < list.Count; i++)
                if (String.Equals(list[i].Family, f, StringComparison.OrdinalIgnoreCase)) return list[i];
            return list[0];
        }

        public static FontFamily ToFontFamily(string family)
        {
            string f = Normalize(family);
            // 系统默认：Segoe UI，中文由系统字体回退（W10M 上是微软雅黑 UI）显示
            return new FontFamily(f.Length == 0 ? "Segoe UI" : f);
        }

        private static Size Measure(string family)
        {
            try
            {
                var block = new TextBlock
                {
                    Text = "WMwm iil 0123456789 永和九年，岁在癸丑",
                    FontSize = 40,
                    FontFamily = new FontFamily(family)
                };
                block.Measure(new Size(Double.PositiveInfinity, Double.PositiveInfinity));
                return block.DesiredSize;
            }
            catch (Exception)
            {
                return new Size(0, 0);
            }
        }
    }
}
