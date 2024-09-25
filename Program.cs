using Newtonsoft.Json.Linq;
using ProcessFiles_Demo.Client;
using ProcessFiles_Demo.Commands;
using ProcessFiles_Demo.FileProcessing;
using ProcessFiles_Demo.Helpers;
using ProcessFiles_Demo.Logging;
using ProcessFiles_Demo.Decryption;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    private static readonly string ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");

    static async Task Main(string[] args)
    {
        string processor_type = "punchexport";

        // Load client settings
        var clientSettings = LoadClientSettings(processor_type);

        // Extract FTP/SFTP settings
        string protocol = clientSettings["FTPSettings"]["Protocol"].ToString();
        string host = clientSettings["FTPSettings"]["Host"].ToString();
        int port = (int)clientSettings["FTPSettings"]["Port"];
        string username = clientSettings["FTPSettings"]["Username"].ToString();
        string password = clientSettings["FTPSettings"]["Password"].ToString();

        // Extract folder paths
        string reprocessingFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["ReprocessingFolder"].ToString());
        string failedFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["FailedFolder"].ToString());
        string processedFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["ProcessedFolder"].ToString());
        string outputFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["outputFolder"].ToString());
        string decryptedFolderOutput = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["decryptedFolderOutput"].ToString());

        // Ensure directories exist
        Directory.CreateDirectory(reprocessingFolder);
        Directory.CreateDirectory(failedFolder);
        Directory.CreateDirectory(processedFolder);
        Directory.CreateDirectory(outputFolder);
        Directory.CreateDirectory(decryptedFolderOutput);

        // Initialize file transfer client
        var fileTransferClient = FileTransferClientFactory.CreateClient(protocol, host, port, username, password);

        // 1. Process any files in the Reprocessing folder first
        await ProcessReprocessingFilesAsync(fileTransferClient, processor_type, reprocessingFolder, processedFolder, failedFolder, outputFolder, decryptedFolderOutput, clientSettings);

        // 2. Fetch and process files from FTP/SFTP
        await FetchAndProcessFilesAsync(fileTransferClient, processor_type, processedFolder, reprocessingFolder, outputFolder, decryptedFolderOutput, clientSettings);

        // 3. Process any remaining files in the Reprocessing folder again
        // await ProcessReprocessingFilesAsync(fileTransferClient, processor_type, reprocessingFolder, processedFolder, failedFolder, outputFolder, decryptedFolderOutput, clientSettings);
    }

    private static async Task FetchAndProcessFilesAsync(IFileTransferClient fileTransferClient, string processor_type, string processedFolder, string reprocessingFolder, string outputFolder, string decryptedFolderOutput, JObject clientSettings)
    {
        var processedFiles = new List<string>();

        try
        {
            string remoteDirectoryPath = clientSettings["FTPSettings"]["filePath"].ToString();
            var fileList = await fileTransferClient.ListFilesAsync(remoteDirectoryPath);

            foreach (var remoteFile in fileList)
            {
                // Check if the file is either a PGP or CSV file
                if (remoteFile.EndsWith(".pgp", StringComparison.OrdinalIgnoreCase) || remoteFile.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        // 1. Download the encrypted file from FTP/SFTP
                        string downloadedFilePath = await RetryHelper.RetryAsync(() => fileTransferClient.DownloadAsync($"{remoteDirectoryPath}/{remoteFile}"));

                        string finalFilePath;

                        // 2. Check if decryption is required
                        bool needsDecryption = (bool)clientSettings["DecryptionSettings"]["NeedsDecryption"];

                        if (needsDecryption)
                        {
                            // If decryption is required, decrypt the file
                            string privateKeyPath = clientSettings["DecryptionSettings"]["PrivateKeyPath"].ToString();
                            string passPhrase = clientSettings["DecryptionSettings"]["PassPhrase"].ToString();
                            string decryptedFilePath = Path.Combine(decryptedFolderOutput, Path.GetFileNameWithoutExtension(remoteFile) + ".csv");

                            var decrypt = new Decrypt();
                            finalFilePath = decrypt.DecryptFile(downloadedFilePath, decryptedFilePath, privateKeyPath, passPhrase);

                            LoggerObserver.LogFileProcessed($"Decryption completed for {downloadedFilePath}");
                        }
                        else
                        {
                            // If decryption is not required, use the file as is
                            finalFilePath = downloadedFilePath;
                            LoggerObserver.LogFileProcessed($"No decryption needed for {downloadedFilePath}");
                        }

                        // 3. Process the CSV file using the factory to select the correct processor
                        var csvProcessor = CsvFileProcessorFactory.GetProcessor(processor_type);
                        var processCsvCommand = new ProcessFileCommand(csvProcessor, finalFilePath, outputFolder);
                        await RetryHelper.RetryAsync(() => processCsvCommand.ExecuteAsync());

                        // 4. Move file to Processed folder after successful processing
                        string processedFilePath = MoveFileToFolder(finalFilePath, processedFolder);
                        processedFiles.Add(processedFilePath);
                        LoggerObserver.LogFileProcessed(processedFilePath);

                        // 5. Upload processed CSV back to FTP/SFTP
                        await RetryHelper.RetryAsync(() => fileTransferClient.UploadAsync(processedFilePath, remoteFile));
                    }
                    catch (Exception ex)
                    {
                        string reprocessFilePath = MoveFileToFolder(remoteFile, reprocessingFolder);
                        LoggerObserver.OnFileFailed(reprocessFilePath);
                        LoggerObserver.LogFileProcessed($"ERROR: {ex.Message} - moved to ReprocessFiles.");
                    }
                }
                else
                {
                    LoggerObserver.OnFileFailed($"Not a valid PGP file - {remoteFile}");
                }
            }
        }
        catch (Exception ex)
        {
            LoggerObserver.OnFileFailed("Error processing files from FTP/SFTP");
            LoggerObserver.LogFileProcessed($"ERROR: {ex.Message}");
        }
    }

    private static async Task ProcessReprocessingFilesAsync(IFileTransferClient fileTransferClient, string processor_type, string reprocessingFolder, string processedFolder, string failedFolder, string outputFolder, string decryptedFolderOutput, JObject clientSettings)
    {
        var filesToReprocess = Directory.GetFiles(reprocessingFolder);

        foreach (var file in filesToReprocess)
        {
            try
            {
                string finalFilePath = file;

                // Check if the file is a PGP file and needs decryption
                if (file.EndsWith(".pgp", StringComparison.OrdinalIgnoreCase))
                {
                    bool needsDecryption = (bool)clientSettings["DecryptionSettings"]["NeedsDecryption"];

                    if (needsDecryption)
                    {
                        string privateKeyPath = clientSettings["DecryptionSettings"]["PrivateKeyPath"].ToString();
                        string passPhrase = clientSettings["DecryptionSettings"]["PassPhrase"].ToString();
                        string decryptedFilePath = Path.Combine(decryptedFolderOutput, Path.GetFileNameWithoutExtension(file) + ".csv");

                        var decrypt = new Decrypt();
                        finalFilePath = decrypt.DecryptFile(file, decryptedFilePath, privateKeyPath, passPhrase);
                        LoggerObserver.LogFileProcessed($"Decryption completed for reprocessed file: {file}");
                    }
                }

                // 3. Process the CSV (whether decrypted or raw CSV)
                var csvProcessor = CsvFileProcessorFactory.GetProcessor(processor_type);
                var processCsvCommand = new ProcessFileCommand(csvProcessor, finalFilePath, outputFolder);
                await RetryHelper.RetryAsync(() => processCsvCommand.ExecuteAsync());

                // Move to Processed folder if successful
                string processedFilePath = MoveFileToFolder(finalFilePath, processedFolder);
                LoggerObserver.LogFileProcessed(processedFilePath);
            }
            catch (Exception ex)
            {
                // If it fails again, move to Failed folder
                string failedFilePath = MoveFileToFolder(file, failedFolder);
                LoggerObserver.OnFileFailed(failedFilePath);
                LoggerObserver.LogFileProcessed($"ERROR: {ex.Message} - moved to FailedFiles.");
            }
        }
    }

    private static JObject LoadClientSettings(string clientName)
    {
        string json = File.ReadAllText(ConfigFilePath);
        JObject config = JObject.Parse(json);
        foreach (var client in config["Clients"])
        {
            if (client["ClientName"].ToString() == clientName)
            {
                return (JObject)client;
            }
        }
        throw new Exception("Client not found in the configuration file.");
    }

    private static string MoveFileToFolder(string sourceFilePath, string destinationFolder)
    {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(sourceFilePath);
        string extension = Path.GetExtension(sourceFilePath);
        string dateTimeSuffix = DateTime.Now.ToString("_yyyyMMdd_HHmmss");
        string newFileName = fileNameWithoutExtension + dateTimeSuffix + extension;
        string destinationFilePath = Path.Combine(destinationFolder, newFileName);

        if (File.Exists(destinationFilePath))
        {
            File.Delete(destinationFilePath);
        }

        File.Move(sourceFilePath, destinationFilePath);
        return destinationFilePath;
    }
}
