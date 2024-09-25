using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ProcessFiles_Demo.Client
{
    public interface IFileTransferClient
    {
        Task<IEnumerable<string>> ListFilesAsync(string path); // Add this method
        Task<string> DownloadAsync(string remoteFilePath);
        Task UploadAsync(string localFilePath, string remoteFilePath);
    }

}
