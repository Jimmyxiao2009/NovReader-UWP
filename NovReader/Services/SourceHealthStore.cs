using System;
using System.Threading.Tasks;
using Windows.Storage;

namespace NovReader.Services
{
    /// <summary>书源健康度的本地持久化（source-health.json）。</summary>
    public sealed class SourceHealthStore
    {
        private const string FileName = "source-health.json";

        public async Task LoadAsync(SourceHealth health)
        {
            try
            {
                var item = await ApplicationData.Current.LocalFolder.TryGetItemAsync(FileName);
                var file = item as StorageFile;
                if (file == null) return;
                health.Load(await FileIO.ReadTextAsync(file));
            }
            catch (Exception)
            {
                // 文件损坏时从空白开始
            }
        }

        public async Task SaveAsync(SourceHealth health)
        {
            try
            {
                var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, health.Serialize());
            }
            catch (Exception)
            {
                // 下次搜索结束会再写一次
            }
        }
    }
}
