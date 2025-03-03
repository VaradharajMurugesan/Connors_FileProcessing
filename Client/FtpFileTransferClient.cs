using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using FileTransform.Logging;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace FileTransform.Client
{
    // FTP Client
    public class FtpFileTransferClient : IFileTransferClient
    {
        private readonly string _host;
        private readonly string _username;
        private readonly string _password;

        public FtpFileTransferClient(string host, string username, string password)
        {
            _host = host;
            _username = username;
            _password = password;
        }

        public async Task<IEnumerable<string>> ListFilesAsync(string path)
        {
            FtpWebRequest request = (FtpWebRequest)WebRequest.Create(_host + path);
            request.Method = WebRequestMethods.Ftp.ListDirectory;
            request.Credentials = new NetworkCredential(_username, _password);

            using (FtpWebResponse response = (FtpWebResponse)await request.GetResponseAsync())
            using (StreamReader reader = new StreamReader(response.GetResponseStream()))
            {
                List<string> files = new List<string>();
                string line = null;
                while ((line = reader.ReadLine()) != null)
                {
                    files.Add(line);
                }

                return files;
            }
        }

        public async Task<string> GetLatestFileAsync(string remoteDirectoryPath, string fileNameStartsWith, string fileExtension)
        {
            try
            {
                using (var sftp = new SftpClient(_host, _username, _password))
                {
                    sftp.Connect();

                    // Get the list of files in the directory
                    var files = sftp.ListDirectory(remoteDirectoryPath)
                                    .Where(f => !f.IsDirectory &&
                                                f.Name.StartsWith(fileNameStartsWith, StringComparison.OrdinalIgnoreCase) &&
                                                f.Name.EndsWith(fileExtension, StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(f => f.LastWriteTime)
                                    .ToList();

                    sftp.Disconnect();

                    // If files are found, return the latest file's name
                    if (files.Any())
                    {
                        return files.First().Name;
                    }

                    LoggerObserver.OnFileFailed($"No files matching the criteria found in directory {remoteDirectoryPath}.");
                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                LoggerObserver.OnFileFailed($"An error occurred while fetching the latest file: {ex.Message}");
                return string.Empty;
            }
        }
        public async Task<string> DownloadAsync(string remoteDirectoryPath, string fileNameStartsWith, string fileExtension)
        {
            // Extract the file name from the remote file path
            string fileName = Path.GetFileName(remoteDirectoryPath);

            // Define the local file path using the same file name
            string localFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            var request = (FtpWebRequest)WebRequest.Create(new Uri(new Uri(_host), remoteDirectoryPath));
            request.Method = WebRequestMethods.Ftp.DownloadFile;
            request.Credentials = new NetworkCredential(_username, _password);

            using (var response = (FtpWebResponse)await request.GetResponseAsync())
            using (var responseStream = response.GetResponseStream())
            using (var fileStream = File.Create(localFilePath))
            {
                await responseStream.CopyToAsync(fileStream);
            }

            return localFilePath; // Return the path with the correct file name
        }

        public async Task UploadAsync(string localFilePath, string remoteFilePath)
        {
            var request = (FtpWebRequest)WebRequest.Create(new Uri(new Uri(_host), remoteFilePath));
            request.Method = WebRequestMethods.Ftp.UploadFile;
            request.Credentials = new NetworkCredential(_username, _password);

            using (var fileStream = File.OpenRead(localFilePath))
            using (var requestStream = await request.GetRequestStreamAsync())
            {
                await fileStream.CopyToAsync(requestStream);
            }
        }
    }

    // SFTP Client
    public class SftpFileTransferClient : IFileTransferClient
    {
        private readonly string _host;
        private readonly int _port;
        private readonly string _username;
        private readonly string _password;

        public SftpFileTransferClient(string host, int port, string username, string password)
        {
            _host = host;
            _port = port;
            _username = username;
            _password = password;
        }

        public async Task<IEnumerable<string>> ListFilesAsync(string path)
        {
            using (var sftp = new SftpClient(_host, _port, _username, _password))
            {
                sftp.Connect();

                // List all the files in the specified directory
                var files = sftp.ListDirectory(path).Select(file => file.Name).ToList();

                sftp.Disconnect();

                return await Task.FromResult(files);
            }
        }
        public async Task<string> GetLatestFileAsync(string remoteDirectoryPath, string fileNameStartsWith, string fileExtension)
        {
            try
            {
                using (var sftp = new SftpClient(_host, _port, _username, _password))
                {
                    sftp.Connect();

                    // Get the list of files in the directory
                    var files = sftp.ListDirectory(remoteDirectoryPath)
                                    .Where(f => !f.IsDirectory &&
                                                f.Name.StartsWith(fileNameStartsWith, StringComparison.OrdinalIgnoreCase) &&
                                                f.Name.EndsWith(fileExtension, StringComparison.OrdinalIgnoreCase))
                                    .OrderByDescending(f => f.LastWriteTime)
                                    .ToList();

                    sftp.Disconnect();

                    // If files are found, return the latest file's name
                    if (files.Any())
                    {
                        return files.First().Name;
                    }

                    LoggerObserver.OnFileFailed($"No files matching the criteria found in directory {remoteDirectoryPath}.");
                    return string.Empty;
                }
            }
            catch (Exception ex)
            {
                LoggerObserver.OnFileFailed($"An error occurred while fetching the latest file: {ex.Message}");
                return string.Empty;
            }
        }
        public async Task<string> DownloadAsync(string remoteDirectoryPath, string fileNameStartsWith, string fileExtension)
        {
            try
            {
                using (var sftp = new SftpClient(_host, _port, _username, _password))
                {
                    // Connect to the SFTP server once
                    sftp.Connect();

                    // Get the list of files in the directory and filter based on criteria
                    var files = sftp.ListDirectory(remoteDirectoryPath)
                                     .Where(f => !f.IsDirectory &&
                                                f.Name.StartsWith(fileNameStartsWith, StringComparison.OrdinalIgnoreCase) &&
                                                f.Name.EndsWith(fileExtension, StringComparison.OrdinalIgnoreCase))
                                     .OrderByDescending(f => f.LastWriteTime)
                                     .ToList();

                    // If no files found, return an empty string
                    if (!files.Any())
                    {
                        LoggerObserver.OnFileFailed($"No matching {fileExtension} files found in the specified directory {remoteDirectoryPath}.");
                        sftp.Disconnect();
                        return string.Empty;
                    }

                    // Get the latest file
                    var latestFile = files.First();
                    string remoteFilePath = $"{remoteDirectoryPath.TrimEnd('/')}/{latestFile.Name}";
                    string localFilePath = Path.Combine(Path.GetTempPath(), latestFile.Name);

                    // Log the remote file path for debugging
                    LoggerObserver.Info($"Attempting to download file from: {remoteFilePath}");

                    // Check if the file exists on the remote server
                    if (!sftp.Exists(remoteFilePath))
                    {
                        LoggerObserver.OnFileFailed($"File not found on remote server: {remoteFilePath}");
                        sftp.Disconnect();
                        return string.Empty;
                    }

                    // Download the file
                    using (var fileStream = File.Create(localFilePath))
                    {
                        await Task.Run(() => sftp.DownloadFile(remoteFilePath, fileStream));
                    }

                    // Disconnect the SFTP client
                    sftp.Disconnect();

                    LoggerObserver.Info($"Latest file downloaded: {localFilePath}");
                    return localFilePath;
                }
            }
            catch (SftpPathNotFoundException ex)
            {
                LoggerObserver.OnFileFailed($"File path not found: {ex.Message}");
                return string.Empty;
            }
            catch (Exception ex)
            {
                LoggerObserver.OnFileFailed($"An error occurred while downloading the file: {ex.Message}");
                return string.Empty;
            }
        }




        public async Task UploadAsync(string localFilePath, string remoteFilePath)
        {
            using (var sftp = new SftpClient(_host, _port, _username, _password))
            {
                sftp.Connect();
                using (var fileStream = File.OpenRead(localFilePath))
                {
                    sftp.UploadFile(fileStream, remoteFilePath);
                }
                sftp.Disconnect();
            }
        }
    }
}
