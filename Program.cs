using Newtonsoft.Json.Linq;
using ProcessFiles_Demo.Client;
using ProcessFiles_Demo.Commands;
using ProcessFiles_Demo.FileProcessing;
using ProcessFiles_Demo.Helpers;
using ProcessFiles_Demo.Logging;
using ProcessFiles_Demo.Decryption;
using NLog;
using NLog.Config;
using NLog.Targets;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;

class Program
{
    private static readonly string ConfigFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "appsettings.json");
    //private static readonly Logger Logger = LogManager.LoadConfiguration("NLog.config").GetCurrentClassLogger();
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();


    static async Task Main(string[] args)
    {
        try
        {
            // Set the processType variable based on the arguments
            string processorType = args.Length > 0 ? args[0] : string.Empty;
            string fileNameStartsWith = args.Length > 1 ? args[1] : string.Empty;
            AppDomain.CurrentDomain.SetData("ProcessorType", processorType);

            LoggerObserver.Debug("Application Starting");

            // Define a list of processor types to handle
            var processorTypes = new List<string>();

            if (processorType.Equals("payroll", StringComparison.OrdinalIgnoreCase))
            {
                // Add both payroll and accrualbalanceexport to the processing queue
                processorTypes.Add("payroll");
                processorTypes.Add("accrualbalanceexport");
            }
            else
            {
                // For other processor types, only add the specified processorType
                processorTypes.Add(processorType);
            }

            foreach (var type in processorTypes)
            {
                // Determine the appropriate fileNameStartsWith value for each processorType
                string currentFileNameStartsWith = fileNameStartsWith;

                if (type.Equals("accrualbalanceexport", StringComparison.OrdinalIgnoreCase))
                {
                    // Set fileNameStartsWith explicitly for accrualbalanceexport
                    currentFileNameStartsWith = "AccrualBalanceExport"; // Replace "accrualfile" with the correct prefix if needed
                }

                LoggerObserver.Info($"Starting processing for processor type: {type} with fileNameStartsWith: {currentFileNameStartsWith}");

                // Load client settings
                var clientSettings = ClientSettingsLoader.LoadClientSettings(type);

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
                string mappingFilesFolder = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, clientSettings["Folders"]["mappingFilesFolder"].ToString());

                // Ensure directories exist
                Directory.CreateDirectory(reprocessingFolder);
                Directory.CreateDirectory(failedFolder);
                Directory.CreateDirectory(processedFolder);
                Directory.CreateDirectory(outputFolder);
                Directory.CreateDirectory(decryptedFolderOutput);
                Directory.CreateDirectory(mappingFilesFolder);

                // Initialize file transfer client
                var fileTransferClient = FileTransferClientFactory.CreateClient(protocol, host, port, username, password);

                // Process reprocessing files
                await ProcessReprocessingFilesAsync(fileTransferClient, type, reprocessingFolder, processedFolder, failedFolder, outputFolder, decryptedFolderOutput, clientSettings);

                // Fetch and process files from FTP/SFTP
                await FetchAndProcessFilesAsync(fileTransferClient, type, processedFolder, reprocessingFolder, outputFolder, decryptedFolderOutput, clientSettings, currentFileNameStartsWith);

                LoggerObserver.Info($"Processing completed for processor type: {type}");
            }


            LoggerObserver.Info("Application Completed Successfully");
        }
        catch (Exception ex)
        {
            LoggerObserver.Error(ex, "Application terminated unexpectedly");
            throw;
        }
        finally
        {
            LogManager.Shutdown();
        }
    }


    private static async Task FetchAndProcessFilesAsync(IFileTransferClient fileTransferClient, string processorType, string processedFolder, string reprocessingFolder, string outputFolder, string decryptedFolderOutput, JObject clientSettings, string fileNameStartsWith)
    {
        var processedFiles = new List<string>();

        try
        {
            string remoteDirectoryPath = clientSettings["FTPSettings"]["filePath"].ToString();
            string fileExtension = clientSettings["FTPSettings"]["fileExtension"].ToString();
            bool needsDecryption = (bool)clientSettings["DecryptionSettings"]["NeedsDecryption"];
            
            // Step 1: Download the latest file
            string downloadedFilePath = await RetryHelper.RetryAsync(() => fileTransferClient.DownloadAsync(remoteDirectoryPath, fileNameStartsWith, fileExtension));

            if (string.IsNullOrEmpty(downloadedFilePath))
            {
                LoggerObserver.OnFileFailed("No valid file was downloaded for processing.");
                return;
            }

            try
            {
                string finalFilePath;

                // Step 2: Check if decryption is required               

                if (needsDecryption)
                {
                    // If decryption is required, decrypt the file
                    string privateKeyPath = clientSettings["DecryptionSettings"]["PrivateKeyPath"].ToString();
                    string passPhrase = clientSettings["DecryptionSettings"]["PassPhrase"].ToString();
                    string decryptedFilePath = Path.Combine(decryptedFolderOutput, Path.GetFileNameWithoutExtension(downloadedFilePath) + ".csv");

                    var decrypt = new Decrypt();
                    finalFilePath = decrypt.DecryptFile(downloadedFilePath, decryptedFilePath, privateKeyPath, passPhrase);

                    LoggerObserver.Info($"Decryption completed for {downloadedFilePath}");
                }
                else
                {
                    // If decryption is not required, use the file as is
                    finalFilePath = downloadedFilePath;
                    LoggerObserver.Info($"No decryption needed for {downloadedFilePath}");
                }

                // Step 3: Process the CSV file using the factory to select the correct processor
                var csvProcessor = CsvFileProcessorFactory.GetProcessor(processorType, clientSettings);
                var processCsvCommand = new ProcessFileCommand(csvProcessor, finalFilePath, outputFolder);
                await RetryHelper.RetryAsync(() => processCsvCommand.ExecuteAsync());

                // Step 4: Move file to Processed folder after successful processing
                string processedFilePath = MoveFileToFolder(finalFilePath, processedFolder);
                processedFiles.Add(processedFilePath);
                LoggerObserver.Info($"File successfully processed and moved to: {processedFilePath}");

                // Step 5: Upload processed CSV back to FTP/SFTP
                //await RetryHelper.RetryAsync(() => fileTransferClient.UploadAsync(processedFilePath, Path.GetFileName(processedFilePath)));
            }
            catch (Exception ex)
            {
                if (File.Exists(downloadedFilePath))
                {
                    // Move file to Reprocessing folder on failure
                    string reprocessFilePath = MoveFileToFolder(downloadedFilePath, reprocessingFolder);
                    LoggerObserver.Error(ex, $"Failed to process {reprocessFilePath}: ");
                    LoggerObserver.Info($"ERROR: {ex.Message} - moved to ReprocessFiles.");
                }
                LoggerObserver.Error(ex, $"Exception occured inside the module FetchAndProcessFilesAsync");
            }
        }
        catch (Exception ex)
        {
            LoggerObserver.Error(ex, "Error processing files from FTP/SFTP");
            LoggerObserver.LogFileProcessed($"ERROR: {ex.Message}");
        }
    }


    private static async Task ProcessReprocessingFilesAsync(IFileTransferClient fileTransferClient, string processorType, string reprocessingFolder, string processedFolder, string failedFolder, string outputFolder, string decryptedFolderOutput, JObject clientSettings)
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
                var csvProcessor = CsvFileProcessorFactory.GetProcessor(processorType, clientSettings);
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
                LoggerObserver.Error(ex, $"Failed to reprocess {failedFilePath}");
                LoggerObserver.LogFileProcessed($"ERROR: {ex.Message} - moved to FailedFiles.");
            }
        }
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
