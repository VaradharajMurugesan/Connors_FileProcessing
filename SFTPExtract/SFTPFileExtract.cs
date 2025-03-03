using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json.Linq;
using FileTransform.Logging;
using Renci.SshNet;

namespace FileTransform.SFTPExtract
{
    public class SFTPFileExtract
    {
        public string DownloadAndExtractFile(JObject clientSettings, string remoteDirectoryPath, string localDirectoryPath, string fileNamePrefix)
        {
            // Delete any existing EmployeeAttributeEntity files in the mapping folder
            DeleteExistingMappingFiles(localDirectoryPath);

            // Retrieve SFTP settings
            string host = clientSettings["FTPSettings"]["Host"].ToString();
            string username = clientSettings["FTPSettings"]["Username"].ToString();
            string password = clientSettings["FTPSettings"]["Password"].ToString();
            int port = (int)clientSettings["FTPSettings"]["Port"];

            using (var sftp = new SftpClient(host, port, username, password))
            {
                sftp.Connect();

                // Step 1: List files in the remote directory and find the latest "EmployeeAttributeEntity" .gz file
                var files = sftp.ListDirectory(remoteDirectoryPath)
                                .Where(f => f.Name.StartsWith(fileNamePrefix) && f.Name.EndsWith(".gz") && !f.IsDirectory)
                                .OrderByDescending(f => f.LastWriteTime)
                                .ToList();

                if (!files.Any())
                {
                    LoggerObserver.OnFileFailed("No matching .gz files found in the specified directory.");
                    return "";
                }

                // Select the latest file based on LastWriteTime
                var latestFile = files.First();
                string localFilePath = Path.Combine(localDirectoryPath, latestFile.Name);

                // Step 2: Download the latest file
                using (var fileStream = File.Create(localFilePath))
                {
                    sftp.DownloadFile(latestFile.FullName, fileStream);
                }
                LoggerObserver.LogFileProcessed($"Downloaded file: {latestFile.Name} to {localFilePath}");

                // Disconnect from SFTP
                sftp.Disconnect();

                // Step 3: Extract the .gz file
                string extractedFilePath = Path.Combine(localDirectoryPath, Path.GetFileNameWithoutExtension(latestFile.Name));
                ExtractGzFile(localFilePath, extractedFilePath);
                return extractedFilePath;
            }
        }

        private void DeleteExistingMappingFiles(string mappingFilesFolderPath)
        {
            // Find and delete existing files that start with "EmployeeAttributeEntity" in the specified directory
            var filesToDelete = Directory.GetFiles(mappingFilesFolderPath, "EmployeeEntity*");

            foreach (var file in filesToDelete)
            {
                try
                {
                    File.Delete(file);
                    Console.WriteLine($"Deleted existing mapping file: {file}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to delete file {file}: {ex.Message}");
                }
            }
        }

        private void ExtractGzFile(string gzFilePath, string outputFilePath)
        {
            using (FileStream originalFileStream = new FileStream(gzFilePath, FileMode.Open, FileAccess.Read))
            using (GZipStream decompressionStream = new GZipStream(originalFileStream, CompressionMode.Decompress))
            using (FileStream decompressedFileStream = new FileStream(outputFilePath, FileMode.Create, FileAccess.Write))
            {
                decompressionStream.CopyTo(decompressedFileStream);
                LoggerObserver.LogFileProcessed($"File has been decompressed to: {outputFilePath}");
            }
            File.Delete(gzFilePath);
            LoggerObserver.LogFileProcessed($"Deleted the .gz file: {gzFilePath}");
        }
    }
}
