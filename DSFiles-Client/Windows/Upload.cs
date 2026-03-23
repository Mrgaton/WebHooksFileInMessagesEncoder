using DSFiles_Client.Helpers;
using DSFiles_Shared;
using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Application = Terminal.Gui.App.Application;
using MessageBox = Terminal.Gui.Views.MessageBox;

namespace DSFiles_Client.CGuis
{
    public partial class Upload
    {
        public Upload()
        {
            System.Windows.Forms.OpenFileDialog ofd = new System.Windows.Forms.OpenFileDialog()
            {
                Multiselect = true,
                Title = "Upload Selection.",
                ShowHiddenFiles = true
            };

            if (ofd.ShowDialog() != System.Windows.Forms.DialogResult.OK)
            {
                MessageBox.Query(App!, "DSFiles Manager", "Operation canceled", "Ok");
                App.Dispose();
                return;
            }

            var prog = new Progress()
            {
                ActionToRun = new(async () =>
                {
                    try
                    {
                        WebHookHelper? webHookHelper = null;
                        webHookHelper = new WebHookHelper(Program.client, File.ReadAllText(Program.WebHookFileName));

                        App.Invoke(async () =>
                        {
                        retry:

                            if (!File.Exists(Program.WebHookFileName))
                            {
                                string cp = App.Clipboard.GetClipboardData();

                                if (string.IsNullOrEmpty(cp))
                                {
                                    MessageBox.Query(App!, "DSFiles Manager", "Error copying the clipboard content", "ok :c");
                                    return;
                                }

                                cp = cp.Trim();

                                if (cp.StartsWith("https://", StringComparison.InvariantCultureIgnoreCase) && cp.Contains("webhook", StringComparison.InvariantCultureIgnoreCase))
                                {
                                    var wh = new WebHookHelper(Program.client, cp);

                                    if (wh.channelId <= 0) throw new Exception("WebHook is not valid");

                                    File.WriteAllText(Program.WebHookFileName, cp);

                                    webHookHelper = new WebHookHelper(Program.client, cp);
                                }
                                else
                                {
                                    var res = MessageBox.ErrorQuery(App!, "DSFiles Manager", "Please copy a valid webhook first onto the clipboard first", "Ok", "Retry");

                                    if (res == 1)
                                        goto retry;

                                    return;
                                }
                            }

                            var progress = WindowsHelper.GetProgress();
                            var paths = ofd.FileNames;

                            string filePath = paths[0];

                            if (paths.Length == 1 && Directory.Exists(filePath)) paths = paths.Concat([""]).ToArray();

                            CompressionLevel compLevel = CompressionLevel.Optimal;

                            Stream? stream = null;

                            if (paths.Length == 1)
                            {
                                stream = File.OpenRead(filePath);

                                int reply = (int)MessageBox.Query(App!, "DSFiles Manager", "Do you want to compress this file?", "None", "Fastest", "Optimal", "Smallest");

                                switch (reply)
                                {
                                    case 0:
                                        compLevel = CompressionLevel.NoCompression;
                                        break;

                                    case 1:
                                        compLevel = CompressionLevel.Fastest;
                                        break;

                                    case 2:
                                        compLevel = CompressionLevel.Optimal;
                                        break;

                                    case 3:
                                        compLevel = CompressionLevel.SmallestSize;
                                        break;
                                }
                            }
                            else
                            {
                                string rootPath = ZipCompressor.GetRootPath(paths);

                                ((IProgress<string>)progress).Report("Compressing files please wait");

                                string archivedPath = Path.GetTempFileName();

                                ClientHelper.RemoveOnBoot(archivedPath);

                                Program.SevenZipPaths(archivedPath, CompressionLevel.Optimal, paths);

                                filePath = rootPath.Split('\\').Last(c => !string.IsNullOrEmpty(c)) + ".7z";

                                int tries = 0;

                                while (tries < 5)
                                {
                                    try
                                    {
                                        stream = File.OpenRead(archivedPath);
                                        break;
                                    }
                                    catch (Exception ex)
                                    {
                                        tries++;

                                        Program.WriteException(ref ex);
                                    }
                                }

                                Console.WriteLine();
                            }

                            if (stream == null)
                                throw new ArgumentNullException(nameof(stream));

                            string fileName = Path.GetFileName(filePath);

                            DiscordFilesSpliter.ConsoleProgress = progress;

                            Task.Factory.StartNew(async () =>
                            {
                                var result = await DiscordFilesSpliter.EncodeCore(webHookHelper, fileName, stream, compLevel);

                                await stream.DisposeAsync();

                                /*if (!string.IsNullOrEmpty(API_TOKEN))
                                {
                                    DSServerHelper.AddFile(result.ToJson()).GetAwaiter().GetResult();
                                }*/

                                Program.UploadedFilesWriter.WriteLine(result.UploadLog);

                                try
                                {
                                    ClipClipboard.SetText(result.WebLink);
                                }
                                catch { }

                                ((IProgress<string>)progress).Report("WebLink: " + result.WebLink);
                                ((IProgress<string>)progress).Report("FileSeed: " + result.DownloadToken);
                            });
                        });
                    }
                    catch (Exception ex)
                    {
                        Program.WriteException(ref ex);
                    }
                })
            };

            App.Run(prog);
        }
    }
}