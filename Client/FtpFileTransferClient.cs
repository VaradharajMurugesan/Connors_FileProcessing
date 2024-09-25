using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Renci.SshNet;

namespace ProcessFiles_Demo.Client
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


        public async Task<string> DownloadAsync(string remoteFilePath)
        {
            // Extract the file name from the remote file path
            string fileName = Path.GetFileName(remoteFilePath);

            // Define the local file path using the same file name
            string localFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);

            var request = (FtpWebRequest)WebRequest.Create(new Uri(new Uri(_host), remoteFilePath));
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


        public async Task<string> DownloadAsync(string remoteFilePath)
        {
            string localFilePath = Path.GetTempFileName();

            using (var sftp = new SftpClient(_host, _port, _username, _password))
            {
                sftp.Connect();
                using (var fileStream = File.Create(localFilePath))
                {
                    sftp.DownloadFile(remoteFilePath, fileStream);
                }
                sftp.Disconnect();
            }

            return localFilePath;
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
