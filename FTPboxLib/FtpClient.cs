﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.FtpClient;
using System.Net.FtpClient.Async;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace FTPboxLib
{
    internal class FtpClient : Client
    {
        private System.Net.FtpClient.FtpClient _ftpc;

        private readonly X509Certificate2Collection _certificates;

        public override event EventHandler<ValidateCertificateEventArgs> ValidateCertificate;

        public override bool IsConnected => _ftpc.IsConnected;

        public override string WorkingDirectory
        {
            get
            {
                return _ftpc.GetWorkingDirectory();
            }
            set
            {
                _ftpc.SetWorkingDirectory(value);
                Log.Write(l.Client, "cd {0}", value);
            }
        }

        public FtpClient(AccountController account)
        {
            Controller = account;
            _certificates = new X509Certificate2Collection();
        }

        public override void Connect(bool reconnecting = false)
        {
            Notifications.ChangeTrayText(reconnecting ? MessageType.Reconnecting : MessageType.Connecting);
            Log.Write(l.Debug, "{0} client...", reconnecting ? "Reconnecting" : "Connecting");

            _ftpc = new System.Net.FtpClient.FtpClient
            {
                Host = Controller.Account.Host,
                Port = Controller.Account.Port
            };

            // Add accepted certificates
            _ftpc.ClientCertificates.AddRange(_certificates);

            if (Controller.Account.Protocol == FtpProtocol.FTPS)
            {
                _ftpc.ValidateCertificate += (sender, x) =>
                {
                    var fingerPrint = new X509Certificate2(x.Certificate).Thumbprint;

                    if (_ftpc.ClientCertificates.Count <= 0 && x.PolicyErrors != SslPolicyErrors.None)
                    {
                        _certificates.Add(x.Certificate);
                        x.Accept = false;
                        return;
                    }

                    // if ValidateCertificate handler isn't set, accept the certificate and move on
                    if (ValidateCertificate == null || Settings.TrustedCertificates.Contains(fingerPrint))
                    {
                        Log.Write(l.Client, "Trusted: {0}", fingerPrint);
                        x.Accept = true;
                        return;
                    }

                    var e = new ValidateCertificateEventArgs
                    {
                        Fingerprint = fingerPrint,
                        SerialNumber = x.Certificate.GetSerialNumberString(),
                        Algorithm = x.Certificate.GetKeyAlgorithmParametersString(),
                        ValidFrom = x.Certificate.GetEffectiveDateString(),
                        ValidTo = x.Certificate.GetExpirationDateString(),
                        Issuer = x.Certificate.Issuer
                    };
                    // Prompt user to validate
                    ValidateCertificate?.Invoke(null, e);
                    x.Accept = e.IsTrusted;
                };

                // Change Security Protocol
                if (Controller.Account.FtpsMethod == FtpsMethod.Explicit)
                    _ftpc.EncryptionMode = FtpEncryptionMode.Explicit;
                else if (Controller.Account.FtpsMethod == FtpsMethod.Implicit)
                    _ftpc.EncryptionMode = FtpEncryptionMode.Implicit;
            }

            _ftpc.Credentials = new NetworkCredential(Controller.Account.Username, Controller.Account.Password);

            try
            {
                _ftpc.Connect();
            }
            catch (AuthenticationException) when (_ftpc.ClientCertificates.Count <= 0)
            {
                // Since the ClientCertificates are added when accepted in ValidateCertificate, the first 
                // attempt to connect will fail with an AuthenticationException. If this is the case, a 
                // re-connect is attempted, this time with the certificates properly set.
                // This is a workaround to avoid storing Certificate files locally...
                Connect();
            }

            Controller.HomePath = WorkingDirectory;

            if (IsConnected)
                if (!string.IsNullOrWhiteSpace(Controller.Paths.Remote) && !Controller.Paths.Remote.Equals("/"))
                    WorkingDirectory = Controller.Paths.Remote;

            Log.Write(l.Debug, "Client connected sucessfully");
            Notifications.ChangeTrayText(MessageType.Ready);

            if (Settings.IsDebugMode)
                LogServerInfo();

            // Periodically send NOOP (KeepAlive) to server if a non-zero interval is set            
            SetKeepAlive();
        }

        public override void Disconnect()
        {
            _ftpc.Disconnect();
        }

        public override void Download(string path, string localPath)
        {
            using (Stream file = File.OpenWrite(localPath), rem = _ftpc.OpenRead(path))
            {
                var buf = new byte[8192];
                int read;

                while ((read = rem.Read(buf, 0, buf.Length)) > 0)
                    file.Write(buf, 0, read);
            }
        }

        public override void Download(SyncQueueItem i, string localPath)
        {
            var startedOn = DateTime.Now;
            long transfered = 0;

            using (Stream file = File.OpenWrite(localPath), rem = _ftpc.OpenRead(i.CommonPath))
            {
                var buf = new byte[8192];
                int read;

                while ((read = rem.Read(buf, 0, buf.Length)) > 0)
                {
                    file.Write(buf, 0, read);
                    transfered += read;

                    ReportTransferProgress(new TransferProgressArgs(read, transfered, i, startedOn));

                    ThrottleTransfer(Settings.General.DownloadLimit, transfered, startedOn);
                }
            }
        }

        public override async Task DownloadAsync(SyncQueueItem i, string path)
        {
            var s = await _ftpc.OpenReadAsync(i.CommonPath);

            var startedOn = DateTime.Now;
            long transfered = 0;

            using (Stream file = File.OpenWrite(path))
            {
                var buf = new byte[8192];
                int read;

                while ((read = s.Read(buf, 0, buf.Length)) > 0)
                {
                    file.Write(buf, 0, read);
                    transfered += read;

                    ReportTransferProgress(new TransferProgressArgs(read, transfered, i, startedOn));

                    ThrottleTransfer(Settings.General.DownloadLimit, transfered, startedOn);
                }
            }
        }

        public override void Upload(string localPath, string path)
        {
            using (Stream file = File.OpenRead(localPath), rem = _ftpc.OpenWrite(path))
            {
                var buf = new byte[8192];
                int read;
                long total = 0;


                while ((read = file.Read(buf, 0, buf.Length)) > 0)
                {
                    rem.Write(buf, 0, read);
                    total += read;

                    Console.WriteLine("{0}/{1} {2:p}",
                        total, file.Length,
                        total / (double)file.Length);
                }
            }
        }

        public override void Upload(SyncQueueItem i, string path)
        {
            var startedOn = DateTime.Now;
            long transfered = 0;
            var buf = new byte[8192];

            using (Stream file = File.OpenRead(i.LocalPath), rem = _ftpc.OpenWrite(path))
            {
                int read;

                while ((read = file.Read(buf, 0, buf.Length)) > 0)
                {
                    rem.Write(buf, 0, read);
                    transfered += read;

                    ReportTransferProgress(new TransferProgressArgs(read, transfered, i, startedOn));

                    ThrottleTransfer(Settings.General.UploadLimit, transfered, startedOn);
                }
            }
        }

        public override async Task UploadAsync(SyncQueueItem i, string path)
        {
            var s = await _ftpc.OpenWriteAsync(path);

            var startedOn = DateTime.Now;
            long transfered = 0;

            var buf = new byte[8192];

            using (Stream file = File.Open(i.LocalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int read;
                while ((read = await file.ReadAsync(buf, 0, buf.Length)) > 0)
                {
                    await s.WriteAsync(buf, 0, read);
                    transfered += read;

                    ReportTransferProgress(new TransferProgressArgs(read, transfered, i, startedOn));

                    ThrottleTransfer(Settings.General.UploadLimit, transfered, startedOn);
                }
            }
        }

        public override void SendKeepAlive()
        {
            if (Controller.SyncQueue.Running) return;

            try
            {
                _ftpc.Execute("NOOP");
            }
            catch (Exception ex)
            {
                Common.LogError(ex);
                Reconnect();
            }
        }

        public override void Rename(string oldname, string newname)
        {
            _ftpc.Rename(oldname, newname);
        }

        protected override void CreateDirectory(string cpath)
        {
            _ftpc.CreateDirectory(cpath);
        }

        public override void Remove(string cpath, bool isFolder = false)
        {
            if (isFolder)
            {
                _ftpc.DeleteDirectory(cpath);
            }
            else
            {
                _ftpc.DeleteFile(cpath);
            }
        }

        protected override void LogServerInfo()
        {
            Log.Write(l.Client, "////////////////////Server Info///////////////////");
            Log.Write(l.Client, "System type: {0}", _ftpc.SystemType);
            Log.Write(l.Client, "Encryption Mode: {0}", _ftpc.EncryptionMode);
            Log.Write(l.Client, "Character Encoding: {0}", _ftpc.Encoding);
            Log.Write(l.Client, "//////////////////////////////////////////////////");
        }

        public override void SetFilePermissions(SyncQueueItem i, short mode)
        {
            string command;
            var reply = new FtpReply();

            if (_ftpc.Capabilities.HasFlag(FtpCapability.MFF))
            {
                command = string.Format("MFF UNIX.mode={0}; {1}", mode, i.CommonPath);
                reply = _ftpc.Execute(command);
            }
            if (!reply.Success)
            {
                command = string.Format("SITE CHMOD {0} {1}", mode, i.CommonPath);
                reply = _ftpc.Execute(command);
            }

            if (!reply.Success)
                Log.Write(l.Error, "chmod failed, file: {0} msg: {1}", i.CommonPath, reply.ErrorMessage);
        }

        public override DateTime GetModifiedTime(string cpath)
        {
            return _ftpc.GetModifiedTime(cpath);
        }

        public override void SetModifiedTime(SyncQueueItem i, DateTime time)
        {
            string command;
            var reply = new FtpReply();
            var timeFormatted = time.ToString("yyyyMMddHHMMss");

            if (_ftpc.Capabilities.HasFlag(FtpCapability.MFF))
            {
                command = string.Format("MFF Modify={0}; {1}", timeFormatted, i.CommonPath);
                reply = _ftpc.Execute(command);
            }
            if (!reply.Success && _ftpc.Capabilities.HasFlag(FtpCapability.MFMT))
            {
                command = string.Format("MFMT {0} {1}", timeFormatted, i.CommonPath);
                reply = _ftpc.Execute(command);
            }
            if (!reply.Success)
            {
                command = string.Format("SITE UTIME {0} {1}", timeFormatted, i.CommonPath);
                reply = _ftpc.Execute(command);
            }

            if (!reply.Success)
                Log.Write(l.Error, "SetModTime failed, file: {0} msg: {1}", i.CommonPath, reply.ErrorMessage);
        }

        public override long SizeOf(string path)
        {
            return _ftpc.GetFileSize(path);
        }

        public override bool Exists(string cpath)
        {
            return _ftpc.FileExists(cpath) || _ftpc.DirectoryExists(cpath);
        }

        public override IEnumerable<ClientItem> GetFileListing(string path)
        {
            var list = _ftpc.GetListing(path);
            
            return Array.ConvertAll(list, ConvertItem);
        }

        public override async Task<IEnumerable<ClientItem>> GetFileListingAsync(string path)
        {
            var list = await _ftpc.GetListingAsync(path);

            return Array.ConvertAll(list.ToArray(), ConvertItem);
        }

        /// <summary>
        ///     Throttle the file transfer if speed limits apply.
        /// </summary>
        /// <param name="limit">The download or upload rate to limit to, in kB/s.</param>
        /// <param name="transfered">bytes already transferred.</param>
        /// <param name="startedOn">when did the transfer start.</param>
        private void ThrottleTransfer(int limit, long transfered, DateTime startedOn)
        {
            var elapsed = DateTime.Now.Subtract(startedOn);
            var rate = (int)(elapsed.TotalSeconds < 1 ? transfered : transfered / elapsed.TotalSeconds);
            if (limit > 0 && rate > 1000 * limit)
            {
                double millisecDelay = (transfered / limit - elapsed.Milliseconds);

                if (millisecDelay > int.MaxValue)
                    millisecDelay = int.MaxValue;

                Thread.Sleep((int)millisecDelay);
            }
        }

        /// <summary>
        ///     Convert an FtpItem to a ClientItem
        /// </summary>
        private ClientItem ConvertItem(FtpListItem f)
        {
            var fullPath = f.FullName;
            if (fullPath.StartsWith("./"))
            {
                var cwd = WorkingDirectory;
                var wd = (Controller.Paths.Remote != null && cwd.StartsWithButNotEqual(Controller.Paths.Remote) &&
                          cwd != "/")
                    ? cwd
                    : Controller.GetCommonPath(cwd, false);
                fullPath = fullPath.Substring(2);
                if (wd != "/")
                    fullPath = string.Format("/{0}/{1}", wd, fullPath);
                fullPath = fullPath.Replace("//", "/");
            }

            return new ClientItem
            {
                Name = f.Name,
                FullPath = fullPath,
                Type = GetItemTypeOf(f.Type),
                Size = f.Size,
                LastWriteTime = f.Modified,
                Permissions = f.Permissions()
            };
        }

        /// <summary>
        ///     Convert FtpFileSystemObjectType to ClientItemType
        /// </summary>
        private static ClientItemType GetItemTypeOf(FtpFileSystemObjectType f)
        {
            if (f == FtpFileSystemObjectType.File)
                return ClientItemType.File;
            if (f == FtpFileSystemObjectType.Directory)
                return ClientItemType.Folder;
            return ClientItemType.Other;
        }
    }
}