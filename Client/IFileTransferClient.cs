using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransform.Client
{
    public interface IFileTransferClient
    {
        Task<IEnumerable<string>> ListFilesAsync(string path); // Add this method
        Task<string> GetLatestFileAsync(string remoteDirectoryPath, string fileNameStartsWith, string fileExtension);
        Task<string> DownloadAsync(string remoteDirectoryPath, string fileNameStartsWith, string fileExtension);
        Task UploadAsync(string localFilePath, string remoteFilePath);
    }

}
