using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>搜索历史，最多保留 30 条，最近搜过的排前面。</summary>
    public sealed class SearchHistoryStore
    {
        private const string FileName = "search-history.json";
        private const int MaxItems = 30;

        public async Task<List<string>> LoadAsync()
        {
            var result = new List<string>();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                string text = await FileIO.ReadTextAsync(file);
                var root = LegadoJson.ParseLoose(text);
                if (root == null || root.Kind != JsonKind.Array || root.Items == null) return result;
                foreach (var node in root.Items)
                {
                    string value = node == null ? "" : node.AsString();
                    if (!String.IsNullOrEmpty(value)) result.Add(value);
                }
            }
            catch (Exception)
            {
                // 首次运行没有历史
            }
            return result;
        }

        public async Task SaveAsync(IList<string> history)
        {
            var array = JsonValue.NewArray();
            for (int i = 0; i < history.Count && i < MaxItems; i++)
                array.Items.Add(new JsonValue { Kind = JsonKind.String, Str = history[i] ?? "" });

            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, array.ToJson());
        }
    }
}
