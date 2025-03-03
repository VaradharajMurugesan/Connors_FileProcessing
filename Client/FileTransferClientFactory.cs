using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FileTransform.Client
{
    public static class FileTransferClientFactory
    {
        public static IFileTransferClient CreateClient(string protocol, string host, int port, string username, string password)
        {
            if (protocol.Equals("SFTP", StringComparison.OrdinalIgnoreCase))
            {
                return new SftpFileTransferClient(host, port, username, password);
            }
            else if (protocol.Equals("FTP", StringComparison.OrdinalIgnoreCase))
            {
                return new FtpFileTransferClient(host, username, password);
            }
            else
            {
                throw new ArgumentException("Unsupported protocol specified.");
            }
        }
    }
}
