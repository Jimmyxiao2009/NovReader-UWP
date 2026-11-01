using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;
using NovReader.Models;
using NovReader.Services.Legado;

namespace NovReader.Services
{
    /// <summary>书源本地持久化。文件格式为 Legado 书源数组，可直接与阅读 App 互相导入导出。</summary>
    public sealed class SourceStore
    {
        private const string FileName = "sources.json";

        public async Task<List<BookSource>> LoadAsync()
        {
            var result = new List<BookSource>();
            try
            {
                var file = await ApplicationData.Current.LocalFolder.GetFileAsync(FileName);
                var text = await FileIO.ReadTextAsync(file);
                result.AddRange(LegadoSourceJson.Parse(text));
            }
            catch (Exception)
            {
                // 首次运行或文件损坏时返回空列表
            }
            return result;
        }

        public async Task SaveAsync(IList<BookSource> sources)
        {
            var file = await ApplicationData.Current.LocalFolder.CreateFileAsync(FileName, CreationCollisionOption.ReplaceExisting);
            await FileIO.WriteTextAsync(file, LegadoSourceJson.Serialize(sources));
        }
    }
}
