using DidiSoft.Pgp;

namespace ProcessFiles_Demo.Decryption
{
    public class Decrypt
    {
        public string DecryptFile(string inputFilePath, string outputFilePath, string privateKeyPath, string passPhrase)
        {
            PGPLib pgp = new PGPLib();
            string inputFileLocation = inputFilePath;
            string outputFile = outputFilePath;
            string privateKeyFile = privateKeyPath;
            string privateKeyPassword = passPhrase;

            // Decrypt the file
            pgp.DecryptFile(inputFileLocation, privateKeyFile, privateKeyPassword, outputFile);

            // Return the file path of the decrypted file
            return outputFile;
        }
    }
}
